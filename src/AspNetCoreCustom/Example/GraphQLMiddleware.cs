using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
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

            SendResultToApollo(result, start);

            await WriteResponseAsync(context, result);
        }

        private void SendResultToApollo(ExecutionResult result, DateTime start)
        {
            //var traceResult = result.Extensions.
            var traceQueries = new Dictionary<string, Traces>();
            
            var trace = ConvertApolloTrace(result, start);

            var sampleTrace = new Trace()
            {
                EndTime = DateTime.UtcNow,
                StartTime = DateTime.UtcNow.AddSeconds(2),
                ClientName = "c1",
                ClientVersion = "v1",
            };

            var traces = new Traces();
            traces.trace.Add(trace);


            var traceReport = new FullTracesReport()
            {
                Header = new ReportHeader()
                    {
                        SchemaTag = "current"
                    }
            };

            var rawQuery = Regex.Replace(result.Document.OriginalQuery, @"\t|\n|\r", "");
            var operationName = result.Document.Operations.First().Name;
            var query = "# " + operationName + "\n" + rawQuery;



            //traceReport.TracesPerQueries.Add(rawQuery.ToString(), traces);
            //traceReport.TracesPerQueries.Add("# HeroQuery\nquery HeroQuery { hero { id } }", traces);
            traceReport.TracesPerQueries.Add(query, traces);
            using (Stream file = File.Create("newTest.txt"))
            {
                Serializer.Serialize(file, traceReport);
                file.Close();
            }
        }

        private Trace ConvertApolloTrace(ExecutionResult result, DateTime start)
        {
            var perf = result?.Perf;
            if (perf == null)
            {
                return null;
            }

            var rawTrace = ApolloTracingExtensions.CreateTrace(result.Operation, perf, start);

            var trace = new Trace();
            trace.StartTime = rawTrace.StartTime;
            trace.EndTime = rawTrace.EndTime;
            trace.DurationNs = (ulong)rawTrace.Duration;
            trace.OriginReportedDurationNs = (ulong)rawTrace.Validation.Duration;

            foreach (var resolver in rawTrace.Execution.Resolvers)
            {
                var root = new Trace.Node();

                root.ParentType = resolver.ParentType;
                root.OriginalFieldName = resolver.FieldName;
                root.ResponseName = resolver.FieldName;
                root.StartTime = (ulong)resolver.StartOffset;
                root.Type = resolver.ReturnType;

                trace.Root = root;
            }

            return trace;
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
