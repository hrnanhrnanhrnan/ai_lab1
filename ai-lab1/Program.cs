using Azure.AI.TextAnalytics;
using Microsoft.Extensions.Configuration;
using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Azure;
using Microsoft.Azure.CognitiveServices.Language.LUIS.Runtime;
using Microsoft.Azure.CognitiveServices.Language.LUIS.Runtime.Models;
using System.Collections.Generic;
using System.Net.Http;
using Microsoft.Azure.CognitiveServices.Knowledge.QnAMaker.Models;
using Microsoft.Azure.CognitiveServices.Knowledge.QnAMaker;
using System.Text;

namespace ai_lab1
{
    internal class Program
    {
        //cognitive services variables
        private static string _cogSerKey;
        private static string _cogSerVEndpoint;
        //luis variables
        private static string _luisKey;
        private static string _luisEndpoint;
        private static Guid _luisId;
        private static LUISRuntimeClient _luisRuntimeClient;
        //translator variables
        private static string _translatorKey;
        private static string _translatorEndpoint;
        private static string _location;
        //qna variables
        private static string _primaryQueryEndpointKey;
        private static string _queryingURL;
        private static string _kbId;
        //other
        private static InputCharacteristics _inputCharacteristics;
        private static TextAnalyticsClient _textAnalyticsClient;
        private static HttpClient _httpClient;
        private static QnAMakerRuntimeClient _runtimeClient;

        static async Task Main(string[] args)
        {
            SetupKeysAndCreateClients();

            Console.WriteLine("-------- Welcome to the Höga Kustens Gårdsmusteri Chat Bot --------");
            Console.WriteLine("|        What do you want to know? (write '*quit' to exit)        |");
            Console.WriteLine("-------------------------------------------------------------------");
            
            string userInput = string.Empty;
            _inputCharacteristics = new();
            while (!userInput.ToLower().Contains("*quit"))
            {
                Console.Write("-> ");
                userInput = Console.ReadLine().ToLower();
                try
                {
                    if (!userInput.ToLower().Contains("*quit"))
                    {
                        // gets the language the user input is written in and sets the language property of the instance to the detected language
                        _inputCharacteristics.Language = await GetLanguageAsync(userInput);

                        // if the user input is in english, then the input property of the inputcharacteristics instance is set to the userinput
                        // else the user input is translated from the language detected to english since the qna knowledgebase is in english
                        // and then the input property instance is set to the returned english translation of the user input
                        _inputCharacteristics.Input = _inputCharacteristics.Language.Iso6391Name == "en" 
                            ? userInput
                            : await GetTranslatedTextAsync(userInput, _inputCharacteristics.Language.Iso6391Name, "en");

                        // get a prediction response
                        var predictionRes = await GetPredictionResponseAsync(_inputCharacteristics.Input, "Production");

                        // get intent and entities from the prediction response by passing it as a parameter
                        _inputCharacteristics.Intent = GetTopIntent(predictionRes);
                        _inputCharacteristics.Entities = GetEntities(predictionRes);

                        // get response from the qna maker component by passing the inputcharacteristics instance
                        var response = await GetResponseAsync(_inputCharacteristics);

                        // if the language of the user input is in english then the response is written as it is to the console
                        // else the response is translated to the language of the user input and is then written to the console
                        Console.WriteLine("\t- {0}", (_ = _inputCharacteristics.Language.Iso6391Name == "en"
                            ? response 
                            : await GetTranslatedTextAsync(response, "en", _inputCharacteristics.Language.Iso6391Name)));
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("* {0}", e.Message);
                    Console.WriteLine("...Please try again!");
                }
            }

            _httpClient.Dispose();
        }

        // method to generate response from the qna component
        static async Task<string> GetResponseAsync(InputCharacteristics inputCharacteristics)
        {
            var query = new QueryDTO();

            // the content of the question that is passed to the qna component is the user input in case the preditcted Intent is "None", since that means
            // a chit chat response from the qna is desired
            // in any other case the Intent is passed as a question to the qna component
            query.Question = inputCharacteristics.Intent == "None" ? inputCharacteristics.Input : inputCharacteristics.Intent;

            var response = await _runtimeClient.Runtime.GenerateAnswerAsync(_kbId, query);

            return response.Answers[0].Answer;
        }

        // detect language from the input
        static async Task<DetectedLanguage> GetLanguageAsync(string input)
        {
            var language = await _textAnalyticsClient.DetectLanguageAsync(input);
            return language.Value;
        }

        // parse and sort the entities of the prediction response passed to a dictionary that is returned
        static Dictionary<string, List<string>> GetEntities (PredictionResponse response)
        {
            var entities = new Dictionary<string, List<string>>();
            try
            {
                if (response.Prediction.Entities.Count != 0)
                {
                    foreach (var item in response.Prediction.Entities)
                    {
                        var entitiesFomResponse = JsonConvert.DeserializeObject<List<string>>(item.Value.ToString());
                        entities.Add(item.Key, new List<string> { });
                        for (int i = 0; i < entitiesFomResponse.Count; i++)
                        {
                            entities[item.Key].Add(entitiesFomResponse[i]);
                        }
                    }
                }
            }
            catch (Exception)
            {
                return entities;
            }
            return entities;
        }

        // return the top intent from the prediction response
        static string GetTopIntent(PredictionResponse response)
        {
            return response.Prediction.TopIntent;
        }

        // call to the translation rest api and pass the input as a request body
        // and the Iso6391Name of the language to translate from and the Iso6391Name of the language to translate to
        static async Task<string> GetTranslatedTextAsync(string input, string isoLangFrom, string isoLangTo)
        {
            string translatedText = "";
            string route = $"/translate?api-version=3.0&from={isoLangFrom}&to={isoLangTo}";
            object[] body = new object[] { new { Text = input } };
            var requestBody = JsonConvert.SerializeObject(body);

            using (var request = new HttpRequestMessage())
            {
                request.Method = HttpMethod.Post;
                request.RequestUri = new Uri(_translatorEndpoint + route);
                request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                request.Headers.Add("Ocp-Apim-Subscription-Key", _translatorKey);
                request.Headers.Add("Ocp-Apim-Subscription-Region", _location);

                HttpResponseMessage response = await _httpClient.SendAsync(request).ConfigureAwait(false);
                var result = await response.Content.ReadAsStringAsync();
                JArray jArray = (JArray)JsonConvert.DeserializeObject(result);
                JObject jObject = JObject.Parse(jArray[0].ToString());
                translatedText = jObject["translations"][0]["text"].ToString();
            }
            return translatedText;
        }

        // return the prediction response from the input, and set the log to true to activate active learning
        static async Task<PredictionResponse> GetPredictionResponseAsync(string input, string slotName)
        {
            var request = new PredictionRequest { Query = input };
            var response = await _luisRuntimeClient.Prediction.GetSlotPredictionAsync(_luisId, slotName, request, log: true);
            return response;
        }

        // method to setup keys, endpoints and clients
        static void SetupKeysAndCreateClients()
        {
            try
            {
                IConfigurationBuilder builder = new ConfigurationBuilder().AddJsonFile("appsettings.json");
                IConfigurationRoot config = builder.Build();

                _cogSerKey = config["cogSerKey"];
                _cogSerVEndpoint = config["cogSerEndpoint"];
                _luisKey = config["luisKey"];
                _luisEndpoint = config["luisEndpoint"];
                _luisId = Guid.Parse(config["luisId"]);
                _translatorEndpoint = config["translatorEndpoint"];
                _translatorKey = config["translatorKey"];
                _location = config["location"];
                _primaryQueryEndpointKey = config["primaryQueryEndpointKey"];
                _queryingURL = config["queryingURL"];
                _kbId = config["kbId"];
                _textAnalyticsClient = new(new Uri(_cogSerVEndpoint), new AzureKeyCredential(_cogSerKey));
                _luisRuntimeClient = new(new Microsoft.Azure.CognitiveServices.Language.LUIS.Runtime.ApiKeyServiceClientCredentials(_luisKey))
                { Endpoint = _luisEndpoint };
                _runtimeClient = new(new Microsoft.Azure.CognitiveServices.Knowledge.QnAMaker.EndpointKeyServiceClientCredentials(_primaryQueryEndpointKey))
                { RuntimeEndpoint = _queryingURL };
                _httpClient = new();
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                if (_httpClient != null)
                {
                    _httpClient.Dispose();
                }
                Environment.Exit(0);
            }
        }
    }
}
