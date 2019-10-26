using GraphQL;
using GraphQL.Instrumentation;
using mdg.engine.proto;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using static mdg.engine.proto.Trace;

namespace Example.Tracing
{
    public class ApolloTraceBuilder
    {

        public static async Task<bool> SendResultToApollo(ExecutionResult result)
        {
            var traceResult = GetApolloTraceMetrics(result.Extensions);
            var traceQueries = new Dictionary<string, Traces>();
            var root = GetRootNode(traceResult.Execution);
            var trace = new Trace
            {
                EndTime = traceResult.EndTime,
                StartTime = traceResult.StartTime,
                ClientName = "c1",
                ClientVersion = "v1",
                Root = root
            };
            var traces = new Traces();
            traces.trace.Add(trace);


            var traceReport = new FullTracesReport()
            {
                Header = new ReportHeader()
                {
                    Hostname = "www.example.com",
                    SchemaTag = "current"
                }
            };

            var traceName = $"# {result.Operation.Name}\n{result.Query}";

            //traceReport.TracesPerQueries.Add("# Foo\nquery Foo { user { email } }", traces);
            traceReport.TracesPerQueries.Add(traceName, traces);

            if (result.Operation.Name.ToString()!= "IntrospectionQuery")
            {
                await PostDataToApolloEngine(traceReport);
            }

            return true;
        }

        private static Node GetRootNode(ApolloTrace.ExecutionTrace execution)
        {
            var rootNode = new Node();
            rootNode.ParentType = "query";

            foreach(var resolver in execution.Resolvers)
            {
                rootNode.Childs.Add(GetResolverNode(resolver));
            }

            return rootNode;
        }

        private static Node GetResolverNode(ApolloTrace.ResolverTrace resolver)
        {
            var endTime = resolver.StartOffset + resolver.Duration;

            return new Node()
            {
                ParentType = resolver.ParentType,
                StartTime = (ulong)resolver.StartOffset,
                EndTime = (ulong)endTime,
                Type = resolver.ReturnType,
                ResponseName = resolver.ReturnType,
            };
        }

        private static async Task PostDataToApolloEngine(FullTracesReport traceReport)
        {
            using(var stream = new MemoryStream())
            {
                Serializer.Serialize(stream, traceReport);
                var client = new HttpClient
                {
                    BaseAddress = new Uri("https://engine-report.apollodata.com/")
                };
                client.DefaultRequestHeaders.Add("User-Agent", "apollo-engine-reporting");
                client.DefaultRequestHeaders.Add("X-Api-Key", "service:TracingDemo-2351:d7aPwnVfRZjuTrOmp1XF4g");

                var byteArrayContent = new ByteArrayContent(stream.ToArray());
                byteArrayContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

                var result = await client.PostAsync("api/ingress/traces", byteArrayContent);
                var statusCode = result.StatusCode;
            }
        }

        private static ApolloTrace GetApolloTraceMetrics(Dictionary<string, object> extensions)
        {
            bool wasSuccessful = extensions.TryGetValue("tracing", out object trace);
            if(wasSuccessful)
            {
                return trace as ApolloTrace;
            }

            throw new Exception("No tracing extension configured");
        }
    }
}
