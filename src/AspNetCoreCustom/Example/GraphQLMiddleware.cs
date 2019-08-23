using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using GraphQL;
using GraphQL.Http;
using GraphQL.Instrumentation;
using GraphQL.Types;
using GraphQL.Validation;
using mdg.engine.proto;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using ProtoBuf;
using StarWars;

namespace Example
{
    public class GraphQLMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly GraphQLSettings _settings;
        private readonly IDocumentExecuter _executer;
        private readonly IDocumentWriter _writer;

        public GraphQLMiddleware(
            RequestDelegate next,
            GraphQLSettings settings,
            IDocumentExecuter executer,
            IDocumentWriter writer)
        {
            _next = next;
            _settings = settings;
            _executer = executer;
            _writer = writer;
        }

        public async Task Invoke(HttpContext context, ISchema schema)
        {
            if (!IsGraphQLRequest(context))
            {
                await _next(context);
                return;
            }

            await ExecuteAsync(context, schema);
        }

        private bool IsGraphQLRequest(HttpContext context)
        {
            return context.Request.Path.StartsWithSegments(_settings.Path)
                && string.Equals(context.Request.Method, "POST", StringComparison.OrdinalIgnoreCase);
        }

        private async Task ExecuteAsync(HttpContext context, ISchema schema)
        {
            var start = DateTime.UtcNow;
            var request = Deserialize<GraphQLRequest>(context.Request.Body);

            var result = await _executer.ExecuteAsync(_ =>
            {
                _.Schema = schema;
                _.Query = request?.Query;
                _.OperationName = request?.OperationName;
                _.Inputs = request?.Variables.ToInputs();
                _.UserContext = _settings.BuildUserContext?.Invoke(context);
                _.ValidationRules = DocumentValidator.CoreRules().Concat(new[] { new InputValidationRule() });
                _.EnableMetrics = true;
                _.FieldMiddleware.Use<InstrumentFieldsMiddleware>();

            });

            result.EnrichWithApolloTracing(start);

            SendResultToApollo(result);

            await WriteResponseAsync(context, result);
        }

        private void SendResultToApollo(ExecutionResult result)
        {
            //var traceResult = result.Extensions.
            var traceQueries = new Dictionary<string, Traces>();
            var sampleTrace = new Trace()
            {
                EndTime = DateTime.UtcNow,
                StartTime = DateTime.UtcNow.AddSeconds(2),
                ClientName = "c1",
                ClientVersion = "v1",
            };
            var traces = new Traces();
            traces.trace.Add(sampleTrace);


            var traceReport = new FullTracesReport()
            {
                Header = new ReportHeader()
                    {
                        Hostname = "www.example.com",
                        SchemaTag = "staging"
                    }
            };

            traceReport.TracesPerQueries.Add("# Foo\nquery Foo { user { email } }", traces);
            using (Stream file = File.Create("test.txt"))
            {
                Serializer.Serialize(file, traceReport);
                file.Close();
            }
        }

        private async Task WriteResponseAsync(HttpContext context, ExecutionResult result)
        {
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = result.Errors?.Any() == true ? (int)HttpStatusCode.BadRequest : (int)HttpStatusCode.OK;

            await _writer.WriteAsync(context.Response.Body, result);
        }

        public static T Deserialize<T>(Stream s)
        {
            using (var reader = new StreamReader(s))
            using (var jsonReader = new JsonTextReader(reader))
            {
                var ser = new JsonSerializer();
                return ser.Deserialize<T>(jsonReader);
            }
        }
    }
}
