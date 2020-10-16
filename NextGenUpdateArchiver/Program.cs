using HtmlAgilityPack;
using Newtonsoft.Json;
using NextGenUpdateArchiver.Model;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using NGUThread = NextGenUpdateArchiver.Model.Thread;

namespace NextGenUpdateArchiver
{
    class Program
    {
        static Uri baseAddress = new Uri("https://www.nextgenupdate.com");
        static CookieContainer cookieContainer = new CookieContainer();
        static ConcurrentBag<int> savedUserIds = new ConcurrentBag<int>();
        static HttpClientHandler clientHandler = new HttpClientHandler();
        static HttpClient client = new HttpClient(clientHandler);

        static async Task Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("[err] No argv set.");
                Environment.Exit(-1);
            }

            AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;

            client.BaseAddress = baseAddress;
            clientHandler.CookieContainer = cookieContainer;

            try
            {
                cookieContainer.Add(baseAddress, new Cookie(
                     Environment.GetEnvironmentVariable("NGU_SESSION_NAME"),
                     Environment.GetEnvironmentVariable("NGU_SESSION_VALUE")
                 ));
            }
            catch (Exception ex)
            {
                Console.WriteLine("[warn] Unable to add NGU session cookie. Will proceed as guest (might miss information). Set env variable for NGU_SESSION_NAME and NGU_SESSION_VALUE.");
            }


            switch (args[0].ToLower())
            {
                case "users":
                    await UsersDumpTask();
                    break;
                case "threads":
                    List<Forum> listOfForums = default;
                    if (args.Length == 2)
                    {
                        try
                        {
                            var file = File.ReadAllText(args[1]);
                            listOfForums = JsonConvert.DeserializeObject<List<Forum>>(file);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[err] Failed to parse forum id list. Check json syntax.", ex.Message);
                            Environment.Exit(-1);
                        }
                    }
                    await ThreadsDumpTask(listOfForums);
                    break;
            }

            Console.WriteLine("Done.");
        }

        private static void CurrentDomain_ProcessExit(object sender, EventArgs e)
        {
            using (FileStream fs = new FileStream("users.json", FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            using (StreamWriter writer = new StreamWriter(fs))
            {
                writer.Write(JsonConvert.SerializeObject(savedUserIds));
            }
        }

        static async Task ThreadsDumpTask(List<Forum> forums = default)
        {
            var forumHome = "https://www.nextgenupdate.com/forums/";
            if (!Directory.Exists("threads"))
            {
                Directory.CreateDirectory("threads");
            }

            if (forums == default)
            {
                var response = await client.GetAsync(forumHome);

                if (response.IsSuccessStatusCode)
                {
                    var forumList = new List<Forum>();
                    var doc = new HtmlDocument();
                    doc.LoadHtml(await response.Content.ReadAsStringAsync());

                    var forumsElem = doc.DocumentNode.SelectNodes("//div[starts-with(@id, 'forum')]");

                    forums = await ParseForumListings(forumsElem);

                    File.WriteAllText("forums.json", JsonConvert.SerializeObject(forums));
                }
                else
                {
                    Console.WriteLine("[err] Failed retrieving forum home. Try again.");
                    Environment.Exit(-1);
                }
            }

            //// Now let's dump our threads...
            //foreach (var forum in forums)
            //{
            //    if (forum.ThreadsIds == default)
            //    {
            //        // If we have no threads, we gotta start from the top.
            //        forum.ThreadsIds.Add((await ParseThreadListing(threadsElem)));
            //    }
            //}
        }

        static async Task<List<NGUThread>> ParseThreadListing(HtmlNodeCollection nodes, bool onlyParseThreads = false)
        {
            var threads = new List<NGUThread>();
            foreach (var t in nodes)
            {
                var tid = t.Attributes["id"].Value;

                var thread = new NGUThread();

                // We cant use regex in the xpath with htmlagilitypack so we will do that here...
                if (Regex.IsMatch(tid, "^threadbit_\\d*$"))
                {
                    try
                    {
                        if (!int.TryParse(tid[("threadbit_".Length)..], out var tidOnly))
                        {
                            Console.WriteLine($"[warn] Unable to parse thread id integer from {tid}");
                            continue;
                        }

                        thread.Id = tidOnly;
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Unable to get href for thread id {tid}", ex);
                    }
                }

                threads.Add(thread);

                if (onlyParseThreads)
                {
                    // If we only want to get the thread ids for each forum, stop here.
                    continue;
                }

                // Now let's grab each thread page and post.
                var threadDocument = await ScrapeThread(thread.Id);

                if (threadDocument == null)
                {
                    Console.WriteLine("Unable to fetch thread document.");
                }

                // usernameblock
                // Get all posts.
                var posts = threadDocument.DocumentNode.SelectNodes("//div[starts-with(@id, 'post_')]");

                if (posts == default)
                {
                    throw new Exception("Failed finding posts in thread");
                }

                foreach (var p in posts)
                {
                    var pid = p.Attributes["id"].Value;
                    var post = new Post();

                    // Get post ID.
                    if (Regex.IsMatch(pid, "^post_\\d*$"))
                    {
                        try
                        {
                            if (!int.TryParse(pid[("post_".Length)..], out var pidOnly))
                            {
                                Console.WriteLine($"[warn] Unable to parse post id integer from {pid}");
                                continue;
                            }

                            post.Id = pidOnly;
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"Unable to parse post id {pid}", ex);
                        }
                    }
                    else
                    {
                        // Not a match, silently continue.
                        continue;
                    }

                    // Get poster name.
                    var usernameBlock = p.SelectSingleNode(".//div[@id='usernameblock']");
                    if (usernameBlock == default)
                    {
                        Console.WriteLine($"[warn] No username block found for {post.Id}");
                    }
                    else
                    {
                        post.Poster = usernameBlock.InnerText.Trim();
                    }

                    // Get post date.
                    var panelHeading = p.SelectSingleNode(".//div[@class='panel-heading']");
                    if (panelHeading == default)
                    {
                        Console.WriteLine($"[warn] No panel heading found for {post.Id}");
                    }
                    else
                    {
                        var postDateElem = panelHeading.SelectSingleNode(".//span");
                        // Dumbass hack here because for some reason agility pack likes to return any nested elements in InnerText lol
                        var headingElems = panelHeading.SelectNodes(".//span");
                        if (headingElems == null || headingElems.Count < 2)
                        {
                            Console.Write("[warn] Unable to find span elem for post date.");
                        }
                        else
                        {
                            var actualDateElem = headingElems[0].InnerText;
                            var garbage = headingElems[1].InnerText;
                            var dateDirty = actualDateElem.Replace(garbage, string.Empty);
                            var cleaned = dateDirty.Trim(Environment.NewLine.ToCharArray()).Trim();
                            if (DateTime.TryParse(cleaned, out var postDate))
                            {
                                post.PostDate = postDate;
                            }
                            else
                            {
                                Console.Write($"[warn] Unable to parse post date '{cleaned}' to DateTime.");
                            }
                        }
                    }

                    // Post contents (HTML for now...)
                    var postContentElem = p.SelectSingleNode(".//div[@class='postcontent']");
                    if (postContentElem == default)
                    {
                        Console.WriteLine($"[warn] No post content found for post {post.Id}");
                    }
                    else
                    {
                        post.Contents = postContentElem.InnerHtml;
                    }

                    // Try to get thanks.
                    var thanksBoxElem = threadDocument.DocumentNode.SelectSingleNode($"//li[@id='post_thanks_box_{post.Id}']");
                    if (thanksBoxElem != default)
                    {
                        Console.WriteLine("Found thanks for post");

                        var thanksBoxListElem = thanksBoxElem.SelectSingleNode("//div[@id='nguheader']");
                        if (thanksBoxListElem != default)
                        {
                            var thanksList = thanksBoxListElem.SelectNodes(".//a");
                            if (thanksList != default && thanksList.Count > 0)
                            {
                                post.Thanks.AddRange(
                                    thanksList.Select(a => a.InnerText.Trim())
                                    );
                            }
                        }
                    }
                }
            }

            return threads;
        }

        static async Task<HtmlDocument> ScrapeThread(int id, int tries = 0, bool triggeredLongWait = false)
        {
            if (tries == 5)
            {
                Console.WriteLine("We got stuck quite a few times. Doing a long wait...");
                System.Threading.Thread.Sleep(TimeSpan.FromMinutes(5));
                await ScrapeThread(id, tries++, true);
            }

            var response = await client.GetAsync($"/forums/showthread.php?t={id}");
            if (response.IsSuccessStatusCode)
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(await response.Content.ReadAsStringAsync());
                return doc;
            }
            else if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Console.WriteLine($"Post {id} not found");
            }
            else if (response.StatusCode == HttpStatusCode.BadGateway)
            {
                if (triggeredLongWait)
                {
                    Console.WriteLine("No luck after waiting a long time. Let's try again later.");
                    Environment.Exit(-1);
                }

                // Banned. Let's wait.
                Console.WriteLine("Banned. Waiting 5 seconds...");
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(5));
                await ExtractUser(id, tries++);
            }
            return default;
        }

        static async Task<List<Forum>> ParseForumListings(HtmlNodeCollection nodes)
        {
            var forumList = new List<Forum>();

            foreach (var forum in nodes)
            {
                var id = forum.Attributes["id"].Value;

                // We cant use regex in the xpath with htmlagilitypack so we will do that here...
                if (Regex.IsMatch(id, "^forum\\d*$"))
                {
                    try
                    {
                        if (!int.TryParse(id[5..], out var idOnly))
                        {
                            Console.WriteLine($"[warn] Unable to parse forum id integer from {id}");
                            continue;
                        }

                        var forumLink = forum.SelectSingleNode(".//a");
                        var link = forumLink.Attributes["href"];

                        forumList.Add(await ScrapeForumListing(idOnly));
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Unable to get href for {id}", ex);
                    }
                }
            }

            return forumList;
        }

        static async Task<Forum> ScrapeForumListing(int id, int tries = 0, bool triggeredLongWait = false)
        {
            if (tries == 5)
            {
                Console.WriteLine("We got stuck quite a few times. Doing a long wait...");
                System.Threading.Thread.Sleep(TimeSpan.FromMinutes(5));
                await ScrapeForumListing(id, tries++, true);
            }

            // Ty Beach for leaving the old vBulletin forum links!
            var response = await client.GetAsync($"/forums/forumdisplay.php?f={id}");
            if (response.IsSuccessStatusCode)
            {
                var forum = new Forum(id);
                var doc = new HtmlDocument();
                doc.LoadHtml(await response.Content.ReadAsStringAsync());

                // Read forum name.
                // @Hack... but should work for now.
                var forumNameElem = doc.DocumentNode.SelectSingleNode("//div[@class='col-xs-6 col-md-4 col-lg-5']");
                if (forumNameElem == default)
                {
                    throw new Exception($"[err] Unable to find forum name for {id}");
                }

                var forumNameBlah = forumNameElem.InnerText;

                try
                {
                    var cleanedName = forumNameBlah.Trim(Environment.NewLine.ToCharArray())[7..];
                    forum.Name = cleanedName;
                }
                catch (Exception ex)
                {
                    throw new Exception($"Unable to trim and clean forum name for {id}", ex);
                }

                Console.WriteLine(forum.Name);

                // How many pages?
                var paginator = doc.DocumentNode.SelectSingleNode("//ul[@class='pagination']");
                if (paginator == default)
                {
                    // No paginator so there's only 1 page.
                    Console.WriteLine($"Only 1 page detected for '{forum.Name}'");
                    forum.PageCount = 1;
                }
                else
                {
                    try
                    {
                        var pages = paginator.SelectNodes(".//li");
                        var lastPage = pages[pages.Count - 2];

                        var lastPageParsed = int.Parse(lastPage.InnerText.Trim(Environment.NewLine.ToCharArray()));

                        forum.PageCount = lastPageParsed;
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Unable to parse last page number for {forum.Name}", ex);
                    }
                }

                // Get SubForums
                var cat = doc.DocumentNode.SelectSingleNode("//div[starts-with(@id, 'cat')]");

                if (cat != default)
                {
                    // We are in a category.
                    forum.IsCategory = true;
                    var forums = cat.SelectNodes("//div[starts-with(@id, 'forum')]");
                    forum.SubForums = await ParseForumListings(forums);
                }

                // Get threads.. Here we go.
                var threadsElem = doc.DocumentNode.SelectNodes("//div[starts-with(@id, 'threadbit')]");

                // Set the threads ids.
                forum.ThreadsIds = (await ParseThreadListing(threadsElem)).Select(a => a.Id).ToList();

                return forum;
            }
            else if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Console.WriteLine($"Forum id {id} not found");
            }
            else if (response.StatusCode == HttpStatusCode.BadGateway)
            {
                if (triggeredLongWait)
                {
                    Console.WriteLine("No luck after waiting a long time. Let's try again later.");
                    Environment.Exit(-1);
                }

                // Banned. Let's wait.
                Console.WriteLine("Banned. Waiting 5 seconds...");
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(5));
                await ScrapeForumListing(id, tries++);
            }

            return default;
        }

        static async Task UsersDumpTask()
        {
            if (!Directory.Exists("users"))
            {
                Directory.CreateDirectory("users");
            }

            var startUserId = 1512540; // 1196456;

            var path = "users.json";
            using (FileStream fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read))
            using (StreamReader reader = new StreamReader(fs))
            {
                var content = reader.ReadToEnd();
                if (content != string.Empty)
                {
                    savedUserIds = new ConcurrentBag<int>(
                        JsonConvert.DeserializeObject<List<int>>(
                            content
                            ).OrderBy(i => i)
                        );
                }
            }

            if (savedUserIds.Count != 0)
            {
                startUserId = savedUserIds.Last();
            }

            var enumerateCount = 2000000 - startUserId;
            Console.WriteLine($"Starting at userid {startUserId}. Enumerating {enumerateCount} users.");

            var it = Enumerable.Range(startUserId, enumerateCount);/*.AsParallel();*/

            // We can't parallel here because Cloudflare. Slow drip instead.
            foreach (var userId in it)
            {
                await ExtractUser(userId);
            }

        }

        static async Task ExtractUser(int userId, int tries = 0, bool triggeredLongWait = false)
        {
            if (tries == 5)
            {
                Console.WriteLine("We got stuck quite a few times. Doing a long wait...");
                System.Threading.Thread.Sleep(TimeSpan.FromMinutes(5));
                await ExtractUser(userId, tries++, true);
            }

            var response = await client.GetAsync($"/forums/members/{userId}-username.html");
            if (response.IsSuccessStatusCode)
            {
                File.WriteAllText($"users/{userId}.html", await response.Content.ReadAsStringAsync());
                savedUserIds.Add(userId);
            }
            else if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Console.WriteLine($"{userId} not found");
            }
            else if (response.StatusCode == HttpStatusCode.BadGateway)
            {
                if (triggeredLongWait)
                {
                    Console.WriteLine("No luck after waiting a long time. Let's try again later.");
                    Environment.Exit(-1);
                }

                // Banned. Let's wait.
                Console.WriteLine("Banned. Waiting 5 seconds...");
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(5));
                await ExtractUser(userId, tries++);
            }
        }
    }
}
