#r "System.Web"

using System;

using System.IO;
using System.Web;
using System.Net;
using System.Text;
using System.Configuration;

using System.Net.Http;
using System.Net.Http.Headers;

//----------------------------------------------------------------------------------

static string _trainingKey; // = "b3993dd152464dbc9f5c8abf03908ff7";
static string _projectId; // = "76ed8b15-4053-41aa-93d5-496cb6731516";

static TraceWriter _log;
static HttpClient _client;

public static async Task<HttpResponseMessage> Run(HttpRequestMessage req, TraceWriter log)
{
    _log = log;
    _log.Info("TrainProject has been triggered.");
    
    _trainingKey = ConfigurationManager.AppSettings["TRAINING_KEY"];
    _projectId = ConfigurationManager.AppSettings["PROJECT_ID"];

    try
    {
        string responseString = await TrainProjectRequest();
        _log.Info(responseString);
    }
    catch(Exception e)
    {
        _log.Error(e.Message);
        _log.Error(e.StackTrace);

        return req.CreateResponse(HttpStatusCode.BadRequest, e.Message);
    }
    
    return req.CreateResponse(HttpStatusCode.OK);
}

//----------------------------------------------------------------------------------
        
static async Task<string> TrainProjectRequest()
{
    var client = new HttpClient();
    var queryString = HttpUtility.ParseQueryString(string.Empty);

    _client = new HttpClient();
    _client.DefaultRequestHeaders.Add("Training-key", _trainingKey);

    var uri = "https://southcentralus.api.cognitive.microsoft.com/customvision/v1.1/Training/"
            + $"projects/{_projectId}/train";

    HttpResponseMessage response;

    // Dummy content because Mircosoft
    byte[] bytes = new byte[] {};
    using (var content = new ByteArrayContent(bytes))
    {
        response = await _client.PostAsync(uri, content);
    }

    return await response.Content.ReadAsStringAsync();
}
