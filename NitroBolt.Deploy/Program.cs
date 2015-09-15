using Newtonsoft.Json.Linq;
using NitroBolt.Functional;
using NitroBolt.QSharp;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace NitroBolt.Deploy
{
    //https://www.visualstudio.com/en-us/integrate/api/overview
    //https://{account}.visualstudio.com/_details/security/tokens
    class Program
    {
        static int Main(string[] args)
        {
            try
            {
                if (args.FirstOrDefault() == "--long-running")
                {
                    for (;;)
                    {
                        try
                        {
                            DeployAll();
                        }
                        catch (Exception exc)
                        {
                            Console.Error.WriteLine(exc.ToDisplayMessage());
                        }
                        System.Threading.Thread.Sleep(TimeSpan.FromMinutes(30));
                    }
                }
                DeployAll();
                return 0;
            }
            catch (Exception exc)
            {
                Console.Error.WriteLine(exc.ToDisplayMessage());
                return 1;
            }

        }

        private static void DeployAll()
        {
            var dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

            var settings = QSharp.QParser.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "settings.qs")));
            foreach (var deploy in settings.Where(_node => _node.AsString() == "deploy"))
            {
                var login = deploy.P("login", "*").AsString();
                var password = deploy.P("password", "*").AsString();
                var account = deploy.P("account", "*").AsString();
                var project = deploy.P("project", "*").AsString();
                var targetPath = deploy.P("target-path", "*").AsString();
                var webDeployArchive = deploy.P("web-deploy-package", "*").AsString();
                try
                {
                    //Console.WriteLine(new[] { login, password, account, project, targetPath }.JoinToString(", "));
                    Task.Run(() => Deploy(account, project, login ?? "username", password, targetPath, webDeployArchive)).Wait();
                }
                catch (Exception exc)
                {
                    Console.Error.WriteLine($"{project}: {exc.ToDisplayMessage()}");
                    Log(exc);
                }
            }
        }

        private static void DeployByAppConfig()
        {
            if (true)
            {
                var username = ConfigurationManager.AppSettings["username"] ?? "username";
                var password = ConfigurationManager.AppSettings["password"];
                var account = ConfigurationManager.AppSettings["account"];
                var project = ConfigurationManager.AppSettings["project"];
                var targetPath = ConfigurationManager.AppSettings["target-path"];
                if (string.IsNullOrEmpty(targetPath))
                    throw new ArgumentNullException("target-path");
                Console.WriteLine(targetPath);


                Task.Run(() => Deploy(account, project, username, password, targetPath)).Wait();
            }
        }

        static async Task Deploy(string account, string project, string username, string password, string targetPath, string webDeployArchive = null)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                client.DefaultRequestHeaders
                    .Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(ASCIIEncoding.ASCII.GetBytes($"{username}:{password}")));

                var buildsResponse = await client.GetStringAsync($"https://{account}.visualstudio.com/DefaultCollection/{project}/_apis/build/builds?api-version=2.0&statusFilter=completed&$top=1");
                var jBuildsResponse = JObject.Parse(buildsResponse);
                Log(jBuildsResponse);
                if (jBuildsResponse["value"].Count() == 0)
                    throw new Exception("No builds");
                var jBuild = jBuildsResponse["value"][0];
                var buildid = jBuild["id"];

                if (jBuild["status"]?.ToString() != "completed")
                    throw new Exception($"Build not completed: {jBuild["status"]}");
                if (jBuild["result"]?.ToString() != "succeeded")
                    throw new Exception($"Build failed: {jBuild["result"]}");


                var responseBody = await client.GetStringAsync($"https://{account}.visualstudio.com/DefaultCollection/{project}/_apis/build/builds/{buildid}/artifacts?api-version=2.0");
                var jResponseBody = JObject.Parse(responseBody);
                Log(jResponseBody);

                //Console.WriteLine(responseBody);
                //Console.WriteLine(JObject.Parse(responseBody));
                var downloadUrl = jResponseBody["value"][0]["resource"]["downloadUrl"]?.ToString();
                Console.WriteLine(downloadUrl);

                var zipfile = await client.GetByteArrayAsync(downloadUrl);
                Console.WriteLine(zipfile.Length);
                using (var archive = new ZipArchive(new MemoryStream(zipfile)))
                {
                    if (webDeployArchive != null)
                    {
                        var manifest = System.Xml.Linq.XElement.Load(archive.GetEntry($"drop/{webDeployArchive}.SourceManifest.xml").Open());
                        Log(manifest);
                        var path = manifest.Element("IisApp")?.Attribute("path")?.Value;
                        Console.WriteLine(path);
                        var archivePath = $"Content/{path.Replace("C:", "C_C").Replace('\\', '/')}/";
                        //Console.WriteLine(archivePath);
                        using (var packageArchive = new ZipArchive(archive.GetEntry($"drop/{webDeployArchive}.zip").Open()))
                        {
                            ExtractTo(packageArchive, targetPath, from: archivePath);
                        }
                    }
                    else
                    {
                        ExtractTo(archive, targetPath, from: "drop/");
                    }
                }

            }
        }

        private static void ExtractTo(ZipArchive archive, string targetPath, string from = null)
        {
            if (!Directory.Exists(targetPath))
                Directory.CreateDirectory(targetPath);
            foreach (var entry in archive.Entries)
            {
                var name = SkipStart(entry.FullName, from)?.TrimStart('/');
                if (string.IsNullOrEmpty(name))
                    continue;
                Console.WriteLine(name);
                var fullEntryPath = Path.Combine(targetPath, name);
                if (fullEntryPath.EndsWith("/"))
                {
                    if (!Directory.Exists(fullEntryPath))
                        Directory.CreateDirectory(fullEntryPath);
                }
                else
                {
                    entry.ExtractToFile(fullEntryPath, true);
                }
            }
        }

        static string SkipStart(string path, string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return path;
            if (path.StartsWith(prefix))
                return path.Substring(prefix.Length);
            return null;
        }
        static void Log(object data)
        {
            System.IO.File.AppendAllText(ApplicationHelper.MapPath("log.txt"), data?.ToString());
        }
    }
    public static class ZipArchiveHelper
    {
        public static byte[] ReadAllBytes(this ZipArchiveEntry entry)
        {
            if (entry == null)
                return null;
            using (var stream = entry.Open())
            {
                var result = new byte[entry.Length];
                if (stream.Read(result, 0, result.Length) == result.Length)
                    return result;
                throw new Exception($"Invalid length: '{entry.Name}'");
            }
        }
    }
}
