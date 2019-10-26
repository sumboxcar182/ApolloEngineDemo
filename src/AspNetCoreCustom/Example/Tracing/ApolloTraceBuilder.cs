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

namespace Example.Tracing
{
    public class ApolloTraceBuilder
    {

        public static async Task<bool> SendResultToApollo(ExecutionResult result)
        {
            var traceResult = GetApolloTraceMetrics(result.Extensions);
            var traceQueries = new Dictionary<string, Traces>();
            var sampleTrace = new Trace()
            {
                EndTime = traceResult.EndTime,
                StartTime = traceResult.StartTime,
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
                    SchemaTag = "current"
                }
            };

            traceReport.TracesPerQueries.Add("# Foo\nquery Foo { user { email } }", traces);

            await PostDataToApolloEngine(traceReport);

            using (Stream file = File.Create("test.txt"))
            {
                file.Close();
            }

            return true;
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
                client.DefaultRequestHeaders.Add("Content-Type", "application/x-protobuf");

                var byteArrayContent = new ByteArrayContent(stream.ToArray());
                byteArrayContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

                var result = await client.PostAsync("api/ingress/traces", byteArrayContent);
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
