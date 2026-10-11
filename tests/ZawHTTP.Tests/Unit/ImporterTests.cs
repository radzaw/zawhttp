namespace ZawHTTP.Tests.Unit;

public class ImporterTests
{
    const string Postman = """
        {
          "info": { "name": "Shop API", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
          "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "{{token}}", "type": "string" } ] },
          "variable": [ { "key": "baseUrl", "value": "https://shop.example" }, { "key": "off", "value": "x", "disabled": true } ],
          "item": [
            {
              "name": "Products",
              "item": [
                {
                  "name": "List products",
                  "request": {
                    "method": "GET",
                    "header": [ { "key": "Accept", "value": "application/json" }, { "key": "X-Old", "value": "1", "disabled": true } ],
                    "url": { "raw": "{{baseUrl}}/products?page=1", "host": ["{{baseUrl}}"], "path": ["products"] }
                  }
                },
                {
                  "name": "Create product",
                  "request": {
                    "method": "POST",
                    "auth": { "type": "noauth" },
                    "body": { "mode": "raw", "raw": "{ \"name\": \"Pen\" }", "options": { "raw": { "language": "json" } } },
                    "url": "{{baseUrl}}/products"
                  }
                }
              ]
            },
            {
              "name": "Login",
              "request": {
                "method": "post",
                "auth": { "type": "basic", "basic": [ { "key": "username", "value": "jan" }, { "key": "password", "value": "pw" } ] },
                "body": { "mode": "urlencoded", "urlencoded": [ { "key": "remember", "value": "1" }, { "key": "skip", "value": "x", "disabled": true } ] },
                "url": { "protocol": "https", "host": ["auth", "example"], "port": "8443", "path": ["v1", "login"], "query": [ { "key": "a", "value": "1" } ] }
              }
            },
            {
              "name": "Upload",
              "request": {
                "method": "POST",
                "body": { "mode": "formdata", "formdata": [ { "key": "title", "value": "T", "type": "text" }, { "key": "file", "type": "file", "src": "/tmp/a.png" } ] },
                "url": "{{baseUrl}}/upload"
              }
            },
            {
              "name": "GraphQL",
              "request": {
                "method": "POST",
                "body": { "mode": "graphql", "graphql": { "query": "{ me { id } }", "variables": "{\"x\":1}" } },
                "url": "{{baseUrl}}/graphql"
              }
            }
          ]
        }
        """;

    [Fact]
    public void Imports_a_postman_collection_with_folders_bodies_and_inherited_auth()
    {
        var r = Importer.Import(Postman);
        Assert.Equal("collection", r.Kind);
        var col = Assert.Single(r.Collections!);
        Assert.Equal("collection", col.Type);
        Assert.Equal("Shop API", col.Name);
        Assert.Equal(["Products", "Login", "Upload", "GraphQL"], col.Items!.Select(i => i.Name));

        var folder = col.Items![0];
        Assert.Equal("folder", folder.Type);
        var list = folder.Items![0].Request!;
        Assert.Equal("GET", list.Method);
        Assert.Equal("{{baseUrl}}/products?page=1", list.Url);
        Assert.False(list.Headers![1].Enabled);
        Assert.Equal("bearer", list.Auth!.Type); // inherited from the collection
        Assert.Equal("{{token}}", list.Auth.Token);

        var create = folder.Items[1].Request!;
        Assert.Equal("none", create.Auth!.Type); // "noauth" overrides the inherited auth
        Assert.Equal("raw", create.Body!.Mode);
        Assert.Equal("json", create.Body.RawType);

        var login = col.Items[1].Request!;
        Assert.Equal("POST", login.Method);
        Assert.Equal("https://auth.example:8443/v1/login?a=1", login.Url);
        Assert.Equal(("basic", "jan", "pw"), (login.Auth!.Type, login.Auth.Username, login.Auth.Password));
        Assert.Equal("urlencoded", login.Body!.Mode);
        Assert.False(login.Body.Urlencoded![1].Enabled);

        var upload = col.Items[2].Request!.Body!;
        Assert.Equal("formdata", upload.Mode);
        Assert.Equal(("file", "/tmp/a.png"), (upload.Form![1].Type, upload.Form[1].Value));

        var gql = col.Items[3].Request!.Body!;
        Assert.Equal("json", gql.RawType);
        Assert.Contains("\"query\": \"{ me { id } }\"", gql.Raw);
        Assert.Contains("\"x\": 1", gql.Raw);

        // Collection variables become an environment.
        var env = Assert.Single(r.Environments!);
        Assert.Equal("Shop API variables", env.Name);
        Assert.Equal("https://shop.example", env.Variables[0].Value);
        Assert.False(env.Variables[1].Enabled);
    }

    [Fact]
    public void Imports_a_postman_environment()
    {
        var r = Importer.Import("""{ "name": "Dev", "values": [ { "key": "host", "value": "localhost", "enabled": true }, { "key": "x", "value": "y", "enabled": false } ], "_postman_variable_scope": "environment" }""");
        Assert.Equal("environment", r.Kind);
        var env = Assert.Single(r.Environments!);
        Assert.Equal("Dev", env.Name);
        Assert.Equal(["host", "x"], env.Variables.Select(v => v.Key));
        Assert.False(env.Variables[1].Enabled);
    }

    [Fact]
    public void Imports_a_native_export_with_fresh_ids()
    {
        var r = Importer.Import("""{ "zawhttp": 1, "collections": [ { "id": "c1", "type": "collection", "name": "Mine", "items": [ { "id": "r1", "type": "request", "name": "Ping", "request": { "method": "GET", "url": "http://x" } } ] } ], "environments": [ { "id": "e1", "name": "Local", "variables": [] } ] }""");
        Assert.Equal("workspace", r.Kind);
        var col = Assert.Single(r.Collections!);
        Assert.NotEqual("c1", col.Id);
        Assert.NotEqual("r1", col.Items![0].Id);
        Assert.Equal("http://x", col.Items[0].Request!.Url);
        Assert.NotEqual("e1", Assert.Single(r.Environments!).Id);
    }

    [Fact]
    public void Detects_curl_commands()
    {
        var r = Importer.Import("  curl https://x.example -H 'a: b'");
        Assert.Equal("request", r.Kind);
        Assert.Equal("https://x.example", r.Request!.Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("[1,2]")]
    [InlineData("{\"something\": true}")]
    public void Rejects_unrecognized_input(string text) => Assert.Throws<ApiException>(() => Importer.Import(text));
}
