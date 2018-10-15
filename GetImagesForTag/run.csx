#r "System.Web"
#r "Newtonsoft.Json"

using System;

using System.IO;
using System.Net;
using System.Web;
using System.Text;
using System.Configuration;

using System.Net.Http;
using System.Net.Http.Headers;

using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

//----------------------------------------------------------------------------------

public class Tag
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public int ImageCount { get; set; }

    public Tag(dynamic serializedObject)
    {
        Id = serializedObject.Id.ToString();
        Name = serializedObject.Name.ToString();
        Description = serializedObject.Description.ToString();
        ImageCount = serializedObject.ImageCount;
    }
}

//----------------------------------------------------------------------------------

static string _trainingKey; // = "b3993dd152464dbc9f5c8abf03908ff7";
static string _projectId; // = "76ed8b15-4053-41aa-93d5-496cb6731516";

static TraceWriter _log;
static HttpClient _client;

public static async Task<HttpResponseMessage> Run(HttpRequestMessage req, TraceWriter log)
{
    _log = log;
    _log.Info("C# HTTP trigger function processed a request.");

    // parse query parameter
    string tagName = req.GetQueryNameValuePairs()
        .FirstOrDefault(q => string.Compare(q.Key, "tagName", true) == 0)
        .Value;

    _trainingKey = ConfigurationManager.AppSettings["TRAINING_KEY"];
    _projectId = ConfigurationManager.AppSettings["PROJECT_ID"];

    _client = new HttpClient();
    _client.DefaultRequestHeaders.Add("Training-key", _trainingKey);

    List<string> imageUriList;
    try
    {
        string tagId = await GetTagId(tagName);
        imageUriList = await GetImages(tagId);
    }
    catch (Exception e)
    {
        _log.Error(e.Message);
        _log.Error(e.StackTrace);
        
        return req.CreateResponse(HttpStatusCode.BadRequest, e.Message);
    }

    return req.CreateResponse(HttpStatusCode.OK, JsonConvert.SerializeObject(imageUriList));
}

//----------------------------------------------------------------------------------

public static List<Tag> ParseTagList(List<dynamic> serializedArray)
{
    List<Tag> tagList = new List<Tag>();

    foreach (dynamic json in serializedArray)
    {
        tagList.Add(new Tag(json));
    }

    return tagList;
}

//----------------------------------------------------------------------------------

static async Task<string> GetTagId(string tagName)
{
    // Request parameters
    var uri = "https://southcentralus.api.cognitive.microsoft.com/customvision/v1.1/Training/"
            + $"projects/{_projectId}/tags";

    var response = await _client.GetAsync(uri);

    if (!response.IsSuccessStatusCode)
    {
        throw new Exception($"Failed getting tag list: {await response.Content.ReadAsStringAsync()}");
    }

    string responseString = await response.Content.ReadAsStringAsync();
    dynamic responseObject = JsonConvert.DeserializeObject<dynamic>(responseString);

    List<Tag> tags = ParseTagList(responseObject.Tags.ToObject<List<dynamic>>());

    foreach (Tag tag in tags)
    {
        if (tag.Name == tagName)
        {
            return tag.Id;
        }
    }

    return "";
}

//----------------------------------------------------------------------------------

static async Task<List<string>> GetImages(string tagId)
{
    var queryString = HttpUtility.ParseQueryString(string.Empty);

    // Request parameters
    queryString["tagIds"] = tagId;
    var uri = "https://southcentralus.api.cognitive.microsoft.com/customvision/v1.1/Training/"
           + $"projects/{_projectId}/images/tagged?{queryString}";

    var response = await _client.GetAsync(uri);
    
    string responseString = await response.Content.ReadAsStringAsync();
    dynamic responseObject = JsonConvert.DeserializeObject<dynamic>(responseString);
    
    List<string> imageUriList = new List<string>();
    foreach (dynamic image in responseObject)
    {
        imageUriList.Add(image.ImageUri.ToString());
    }

    return imageUriList;
}