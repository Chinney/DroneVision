#r "System.Web"
#r "Newtonsoft.Json"

using System;

using System.IO;
using System.Net;
using System.Web;
using System.Text;
using System.Configuration;

using Simple.OData.Client;

using System.Net.Http;
using System.Net.Http.Headers;

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

//----------------------------------------------------------------------------------

public class Prediction
{
	public string TagId { get; set; }
	public string Tag { get; set; }
	public double Probability { get; set; }

    public Prediction(dynamic serializedObject)
    {
        TagId = serializedObject.TagId.ToString();
        Tag = serializedObject.Tag.ToString();
        Probability = serializedObject.Probability;
    }
}

//----------------------------------------------------------------------------------

public class Iteration
{
    public string Id { get; set; }
	public string Name { get; set; }
	public bool IsDefault { get; set; }
	public string Status { get; set; }
	public string Created { get; set; }
	public string LastModified { get; set; }
	public string ProjectId { get; set; }
	public bool Exportable { get; set; }

    public Iteration(dynamic serializedObject)
    {
        Id = serializedObject.Id.ToString();
        Name = serializedObject.Name.ToString();
        IsDefault = serializedObject.IsDefault;
        Status = serializedObject.Status.ToString();
        Created = serializedObject.Created.ToString();
        LastModified = serializedObject.LastModified.ToString();
        ProjectId = serializedObject.ProjectId.ToString();
        Exportable = serializedObject.Exportable;
    }
}

//----------------------------------------------------------------------------------

public class InventoryReportLine
{
    public int Header_No { get; set; }
    public string Item_No { get; set; }
    public int XPosition { get; set; }
    public int YPosition { get; set; }
    public int Quantity { get; set; }

    public InventoryReportLine(int inventoryId, string tag, int xPosition, int yPosition)
    {
        Header_No = inventoryId;
        XPosition = xPosition;
        YPosition = yPosition;
        Item_No = tag;
        Quantity = 1;
    }
}
    
//----------------------------------------------------------------------------------

static string _trainingKey;
static string _projectId;

static string _username;
static string _password;

static TraceWriter _log;
static HttpClient _client;
static ODataClient _oClient;

public static async Task<HttpResponseMessage> Run(HttpRequestMessage req, TraceWriter log)
{
    _log = log;
    _log.Info("ClassifyImage has been triggered.");

    _trainingKey = ConfigurationManager.AppSettings["TRAINING_KEY"];
    _projectId = ConfigurationManager.AppSettings["PROJECT_ID"];

    _username = ConfigurationManager.AppSettings["USER"];
    _password = ConfigurationManager.AppSettings["PASSWORD"];

    _client = new HttpClient();
    _client.DefaultRequestHeaders.Add("Training-key", _trainingKey);

    string bodyString = await req.Content.ReadAsStringAsync();
    dynamic bodyObject = JsonConvert.DeserializeObject<dynamic>(bodyString);

    int inventoryId = 0;
    int xPosition = 0;
    int yPosition = 0;

    Prediction finalPrediction;

    try
    {
        inventoryId = Int32.Parse(bodyObject.inventory_id.ToString());
        xPosition = Int32.Parse(bodyObject.xPosition.ToString());
        yPosition = Int32.Parse(bodyObject.yPosition.ToString());
        byte[] imageBytes = Convert.FromBase64String(bodyObject.file.ToString());
        
        string iterationId = await GetIterationId();
        finalPrediction = await ClassifyImage(imageBytes, iterationId);
    }
    catch (Exception e)
    {
        _log.Error(e.Message);
        _log.Error(e.StackTrace);
        
        return req.CreateResponse(HttpStatusCode.BadRequest, e.Message);
    }

    await ReportResult(inventoryId, finalPrediction.Tag, xPosition, yPosition);

    _log.Info($"Tag: {finalPrediction.Tag} - Probability: {finalPrediction.Probability}");
    return req.CreateResponse(HttpStatusCode.OK, JsonConvert.SerializeObject(finalPrediction));
}

//----------------------------------------------------------------------------------

public static List<Iteration> ParseIterationList(List<dynamic> serializedArray)
{
    List<Iteration> iterationList = new List<Iteration>();

    foreach (dynamic json in serializedArray)
    {
        iterationList.Add(new Iteration(json));
    }

    return iterationList;
}

//----------------------------------------------------------------------------------

static async Task<string> GetIterationId()
{
    var uri = "https://southcentralus.api.cognitive.microsoft.com/customvision/v1.1/Training/"
           + $"projects/{_projectId}/iterations";

    var response = await _client.GetAsync(uri);

    string responseString = await response.Content.ReadAsStringAsync();
    dynamic responseObject = JsonConvert.DeserializeObject<dynamic>(responseString);
    List<Iteration> iterationList = ParseIterationList(responseObject.ToObject<List<dynamic>>());

    string iterationId = "";

    foreach (Iteration iteration in iterationList)
    {
        if (iteration.Status == "Completed")
        {
            iterationId = iteration.Id;
        }
    }

    if (iterationId == "")
    {
        throw new Exception("Could not get a valid iterationId.");
    }

    return iterationId;
}

//----------------------------------------------------------------------------------

public static List<Prediction> ParsePredictionList(List<dynamic> serializedArray)
{
    List<Prediction> predictionList = new List<Prediction>();

    foreach (dynamic json in serializedArray)
    {
        predictionList.Add(new Prediction(json));
    }

    return predictionList;
}

//----------------------------------------------------------------------------------

static async Task<Prediction> ClassifyImage(byte[] imageBytes, string iterationId)
{
    var queryString = HttpUtility.ParseQueryString(string.Empty);

    queryString["iterationId"] = iterationId;

    var uri = "https://southcentralus.api.cognitive.microsoft.com/customvision/v1.1/Training/"
           + $"projects/{_projectId}/"
           + $"quicktest/image?{queryString}";

    HttpResponseMessage response;

    using (var content = new ByteArrayContent(imageBytes))
    {
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        response = await _client.PostAsync(uri, content);
    }

    string responseString = await response.Content.ReadAsStringAsync();
    dynamic responseObject = JsonConvert.DeserializeObject<dynamic>(responseString);
    List<Prediction> predictionList = ParsePredictionList(responseObject.Predictions.ToObject<List<dynamic>>());

    double currentMax = 0;
    Prediction currentPrediction = null;
    foreach (Prediction prediction in predictionList)
    {
        if (prediction.Probability > currentMax)
        {
            currentMax = prediction.Probability;
            currentPrediction = prediction;
        }
    }

    if (currentPrediction == null)
    {
        throw new Exception("Failed to Predict for unknown reasons.");
    }

    return currentPrediction;
}

//----------------------------------------------------------------------------------

static async Task ReportResult(int inventoryId, string tag, int xPosition, int yPosition)
{
    string uri = "https://api.businesscentral.dynamics.com/v1.0/6ad1c7ec-a927-4666-b1f6-f4b32183378b/ODataV4/Company('CRONUS%20DE')/InventoryReportDroneLines";

    string encoded = System.Convert.ToBase64String(System.Text.Encoding.GetEncoding("ISO-8859-1").GetBytes(_username + ":" + _password));
    
    if (_client.DefaultRequestHeaders.Authorization == null)
    {
        _client.DefaultRequestHeaders.Add("Authorization", "Basic " + encoded);
    }

    var inventoryReportLine = new InventoryReportLine(inventoryId, tag, xPosition, yPosition);

    var requestContent = new StringContent(JsonConvert.SerializeObject(inventoryReportLine), Encoding.UTF8, "application/json");

    HttpResponseMessage response = await _client.PostAsync(uri, requestContent);

    _log.Info(await response.Content.ReadAsStringAsync());
}