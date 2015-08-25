using Newtonsoft.Json.Linq;
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
        static void Main(string[] args)
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
        static async Task Deploy(string account, string project, string username, string password, string targetPath)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                client.DefaultRequestHeaders
                    .Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(ASCIIEncoding.ASCII.GetBytes($"{username}:{password}")));

                var buildsResponse = await client.GetStringAsync($"https://{account}.visualstudio.com/DefaultCollection/{project}/_apis/build/builds?api-version=2.0&statusFilter=completed&$top=1");
                var buildid = JObject.Parse(buildsResponse)["value"][0]["id"];

                var responseBody = await client.GetStringAsync($"https://{account}.visualstudio.com/DefaultCollection/{project}/_apis/build/builds/{buildid}/artifacts?api-version=2.0");

                //Console.WriteLine(responseBody);
                //Console.WriteLine(JObject.Parse(responseBody));
                var downloadUrl = JObject.Parse(responseBody)["value"][0]["resource"]["downloadUrl"]?.ToString();
                Console.WriteLine(downloadUrl);

                var zipfile = await client.GetByteArrayAsync(downloadUrl);
                Console.WriteLine(zipfile.Length);
                using (var archive = new ZipArchive(new MemoryStream(zipfile)))
                {
                    ExtractTo(archive, targetPath, from:"drop/");
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
    }
}
