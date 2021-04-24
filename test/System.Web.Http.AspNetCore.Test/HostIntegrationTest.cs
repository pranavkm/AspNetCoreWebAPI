// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Newtonsoft.Json.Linq;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using System.Net.Http.Json;

namespace System.Web.Http.AspNetCore
{
    public class HostIntegrationTest
    {
        [Fact]
        public async Task SimpleGet_Works()
        {
            using var host = await GetHostAsync();
            var testServer = host.GetTestServer();
            var client = testServer.CreateClient();

            var response = await client.GetAsync("HelloWorld");

            Assert.True(response.IsSuccessStatusCode);
            Assert.Equal("\"Hello from ASP.NET Core\"", await response.Content.ReadAsStringAsync());
            Assert.Null(response.Headers.TransferEncodingChunked);
        }

        [Fact]
        public async Task SimplePost_Works()
        {
            using var host = await GetHostAsync();
            var testServer = host.GetTestServer();
            var client = testServer.CreateClient();

            var content = new StringContent("\"Echo this\"", Encoding.UTF8, "application/json");

            var response = await client.PostAsync("Echo", content);

            Assert.True(response.IsSuccessStatusCode);
            Assert.Equal("\"Echo this\"", await response.Content.ReadAsStringAsync());
            Assert.Null(response.Headers.TransferEncodingChunked);
        }

        [Fact]
        public async Task GetThatThrowsDuringSerializations_RespondsWith500()
        {
            using var host = await GetHostAsync();
            var testServer = host.GetTestServer();
            var client = testServer.CreateClient();

            var response = await client.GetAsync("Error");

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            JObject json = Assert.IsType<JObject>(JToken.Parse(await response.Content.ReadAsStringAsync()));
            JToken exceptionMessage;
            Assert.True(json.TryGetValue("ExceptionMessage", out exceptionMessage));
            Assert.Null(response.Headers.TransferEncodingChunked);
        }

        [Fact]
        public async Task SystemTextJsonFormattersWorks()
        {
            using var host = await GetHostAsync(typeof(TestStartupWithSystemTextJsonFormatting));
            var testServer = host.GetTestServer();
            var client = testServer.CreateClient();

            // We should be able to round-trip values using the new S.T.J extensions
            var response = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client, "Echo/EchoPoco", new SomePoco
            {
                Id = 15,
                Name = "test123",
            });

            var echo = await response.Content.ReadFromJsonAsync<SomePoco>();
            Assert.Equal(15, echo.Id);
            Assert.Equal("test123", echo.Name);
        }

        private class TestStartup
        {
            public void Configure(IApplicationBuilder appBuilder)
            {
                var config = new HttpConfiguration();
                config.Routes.MapHttpRoute("Default", "{controller}");
                appBuilder.UseWebApi(config);
            }
        }

        private class TestStartupWithSystemTextJsonFormatting
        {
            public void Configure(IApplicationBuilder appBuilder)
            {
                var config = new HttpConfiguration();
                config.Formatters.Insert(0, new Net.Http.Formatting.SystemTextJsonMediaTypeFormatter());
                config.Routes.MapHttpRoute("Default", "{controller}/{action}");
                appBuilder.UseWebApi(config);
            }
        }

        private static async Task<IHost> GetHostAsync(Type startup = null)
        {
            startup ??= typeof(TestStartup);
            var host = new HostBuilder()
                .UseEnvironment(Environments.Development)
                .ConfigureWebHost(b => b.UseStartup(startup).UseTestServer())
                .Build();
            await host.StartAsync();
            return host;
        }
    }

    public class HelloWorldController : ApiController
    {
        public string Get()
        {
            return "Hello from ASP.NET Core";
        }
    }

    public class EchoController : ApiController
    {
        public string Post([FromBody] string s)
        {
            return s;
        }

        [HttpPost]
        public SomePoco EchoPoco([FromBody] SomePoco value) => value;
    }

    public class SomePoco
    {
        public int Id { get; set; }

        public string Name { get; set; }
    }

    public class ErrorController : ApiController
    {
        public ExceptionThrower Get()
        {
            return new ExceptionThrower();
        }

        public class ExceptionThrower
        {
            public string Throws
            {
                get
                {
                    throw new InvalidOperationException();
                }
            }
        }
    }
}
