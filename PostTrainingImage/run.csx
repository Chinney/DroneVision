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

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

using System.Threading.Tasks;

using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

public class InMemoryMultipartFormDataStreamProvider : MultipartStreamProvider
{
    private NameValueCollection _formData = new NameValueCollection();
    private List<HttpContent> _fileContents = new List<HttpContent>();

    // Set of indexes of which HttpContents we designate as form data
    private Collection<bool> _isFormData = new Collection<bool>();

    /// <summary>
    /// Gets a <see cref="NameValueCollection"/> of form data passed as part of the multipart form data.
    /// </summary>
    public NameValueCollection FormData {
        get { return _formData; }
    }

    /// <summary>
    /// Gets list of <see cref="HttpContent"/>s which contain uploaded files as in-memory representation.
    /// </summary>
    public List<HttpContent> Files {
        get { return _fileContents; }
    }

    public override Stream GetStream(HttpContent parent, HttpContentHeaders headers) {
        // For form data, Content-Disposition header is a requirement
        ContentDispositionHeaderValue contentDisposition = headers.ContentDisposition;
        if (contentDisposition != null) {
            // We will post process this as form data
            _isFormData.Add(String.IsNullOrEmpty(contentDisposition.FileName));

            return new MemoryStream();
        } else {
            _isFormData.Add(false);
            return new MemoryStream();
        }

        // If no Content-Disposition header was present.
        throw new InvalidOperationException(string.Format("Did not find required '{0}' header field in MIME multipart body part..", "Content-Disposition"));
    }

    /// <summary>
    /// Read the non-file contents as form data.
    /// </summary>
    /// <returns></returns>
    public override async Task ExecutePostProcessingAsync() {
        // Find instances of non-file HttpContents and read them asynchronously
        // to get the string content and then add that as form data
        for (int index = 0; index < Contents.Count; index++) {
            if (_isFormData[index]) {
                HttpContent formContent = Contents[index];
                // Extract name from Content-Disposition header. We know from earlier that the header is present.
                ContentDispositionHeaderValue contentDisposition = formContent.Headers.ContentDisposition;
                string formFieldName = UnquoteToken(contentDisposition.Name) ?? String.Empty;

                // Read the contents as string data and add to form data
                string formFieldValue = await formContent.ReadAsStringAsync();
                FormData.Add(formFieldName, formFieldValue);
            } else {
                _fileContents.Add(Contents[index]);
            }
        }
    }

    /// <summary>
    /// Remove bounding quotes on a token if present
    /// </summary>
    /// <param name="token">Token to unquote.</param>
    /// <returns>Unquoted token.</returns>
    private static string UnquoteToken(string token) {
        if (String.IsNullOrWhiteSpace(token)) {
            return token;
        }

        if (token.StartsWith("\"", StringComparison.Ordinal) && token.EndsWith("\"", StringComparison.Ordinal) && token.Length > 1) {
            return token.Substring(1, token.Length - 2);
        }

        return token;
    }
}

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
    _log.Info("PostTrainingImage has been triggered.");
    
    _trainingKey = ConfigurationManager.AppSettings["TRAINING_KEY"];
    _projectId = ConfigurationManager.AppSettings["PROJECT_ID"];

    string tagName = req.GetQueryNameValuePairs()
        .FirstOrDefault(q => string.Compare(q.Key, "tagName", true) == 0)
        .Value;

    string tagDescription = req.GetQueryNameValuePairs()
        .FirstOrDefault(q => string.Compare(q.Key, "tagDescription", true) == 0)
        .Value;

    if (tagName == null)
    {
        _log.Error("Need tagName as query parameter!");
        return req.CreateResponse(HttpStatusCode.BadRequest, "Need tagName as query parameter!");
    }

    if (tagDescription == null)
    {
        tagDescription = "";
    }

    _client = new HttpClient();
    _client.DefaultRequestHeaders.Add("Training-key", _trainingKey);

    //Check if submitted content is of MIME Multi Part Content with Form-data in it?
    if (!req.Content.IsMimeMultipartContent("form-data"))
    {
        _log.Error("Could not find file to upload");
        return req.CreateResponse(HttpStatusCode.BadRequest, "Could not find file to upload");
    }

    try
    {
        //Read the content in a InMemory Muli-Part Form Data format
        var provider = await req.Content.ReadAsMultipartAsync(new InMemoryMultipartFormDataStreamProvider());
        //Get the first file
        var files = provider.Files;
        var uploadedFile = files[0];

        //Upload anonymous image from camera control to powerappimages blob
        using (Stream fileStream = await uploadedFile.ReadAsStreamAsync()) //as Stream is IDisposable
        {
            MemoryStream ms = new MemoryStream();
            fileStream.CopyTo(ms);

            byte[] imageBytes = ms.ToArray();
            string tagId = await GetTagId(tagName, tagDescription);
            await PostImage(imageBytes, tagId);
        }

        await TrainProject();
    }
    catch (Exception e)
    {
        _log.Error(e.Message);
        _log.Error(e.StackTrace);
        
        return req.CreateResponse(HttpStatusCode.BadRequest, e.Message);
    }
    
    _log.Info("Success!");
    return req.CreateResponse(HttpStatusCode.OK, "Success!");
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

//---------------------------------------------------------------------------------------------

static async Task<string> GetTag(string tagName)
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

//-----------------------------------------------------------------------------------

static async Task<string> CreateTag(string tagName, string tagDescription)
{
    var queryString = HttpUtility.ParseQueryString(string.Empty);

    // Request parameters
    queryString["description"] = tagDescription;

    var uri = "https://southcentralus.api.cognitive.microsoft.com/customvision/v1.1/Training/"
            + $"projects/{_projectId}/"
            + $"tags?name={tagName}&{queryString}";

    HttpResponseMessage response;

    // dummy content because Microsoft
    byte[] bytes = new byte[] {};
    using (var content = new ByteArrayContent(bytes))
    {
        response = await _client.PostAsync(uri, content);
    }

    if(!response.IsSuccessStatusCode)
    {
        throw new Exception($"Failed creating tag: {await response.Content.ReadAsStringAsync()}");
    }

    string responseString = await response.Content.ReadAsStringAsync();
    dynamic responseObject = JsonConvert.DeserializeObject<dynamic>(responseString);
    Tag tag = new Tag(responseObject);

    return tag.Id;
}

//----------------------------------------------------------------------------------

static async Task<string> GetTagId(string tagName, string tagDescription)
{
    string tagId = await GetTag(tagName);

    if (tagId == "")
    {
        tagId = await CreateTag(tagName, tagDescription);
    }

    if (tagId == "")
    {
        throw new Exception("Failed getting tagId for unknown reasons");
    }

    return tagId;
}

//----------------------------------------------------------------------------------

static async Task PostImage(byte[] imageBytes, string tagId)
{
    var queryString = HttpUtility.ParseQueryString(string.Empty);

    // Request parameters
    queryString["tagIds"] = tagId;

    var uri = "https://southcentralus.api.cognitive.microsoft.com/customvision/v1.1/Training/"
           + $"projects/{_projectId}/"
           + $"images/image?{queryString}";

    HttpResponseMessage response;
    
    using (var content = new ByteArrayContent(imageBytes))
    {
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        response = await _client.PostAsync(uri, content);
    }
}

//----------------------------------------------------------------------------------

static async Task TrainProject()
{
    var uri = "https://dronevisionmunichhack.azurewebsites.net/api/TrainProject"
            + "?code=MJ6HArHZ7rIX6SJ1uwtOiyFnihOhTYggmDl1hZ9wfFavD/cO/a6puA==";
    var response = await _client.GetAsync(uri);

    _log.Info(await response.Content.ReadAsStringAsync());
}