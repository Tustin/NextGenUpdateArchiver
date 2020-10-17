using HtmlAgilityPack;
using Newtonsoft.Json;
using NextGenUpdateArchiver.Model;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        static ConcurrentBag<Profile> savedUsers = new ConcurrentBag<Profile>();
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
            catch (Exception)
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
                writer.Write(JsonConvert.SerializeObject(savedUsers));
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
                var response = await Get(forumHome);

                if (response.IsSuccessStatusCode)
                {
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
        }

        static async Task<List<NGUThread>> ParseThreadListing(Forum forum, HtmlNodeCollection nodes = default, bool onlyParseThreads = false)
        {
            var threads = new List<NGUThread>();

            // var threadsElem = doc.DocumentNode.SelectNodes("//div[starts-with(@id, 'threadbit')]");

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
                        throw new Exception($"Unable to get thread id {tid}", ex);
                    }
                }
                else
                {
                    // Not a threadbit, silently continue.
                    continue;
                }

                threads.Add(thread);

                var threadTitleElem = t.SelectSingleNode(".//div[contains(@class, 'threadbit_thread_title')]");

                if (threadTitleElem == default)
                {
                    throw new Exception($"Unable to get thread name for thread {tid}");
                }

                thread.Title = threadTitleElem.InnerText.Trim();

                var isStuckElem = t.SelectSingleNode(".//span[contains(@class,'label label-info')]");
                if (isStuckElem != default)
                {
                    thread.Stickied = true;
                }

                var isClosedElem = t.SelectSingleNode(".//span[contains(@class,'label label-danger')]");
                if (isClosedElem != default)
                {
                    thread.Closed = true;
                }

                // Try to get views.                                            v lol
                var viewsElem = t.SelectSingleNode(".//div[@style='margin-bottom: 2px; font-size: 15px;']");
                if (viewsElem != default)
                {
                    var matches = Regex.Match(viewsElem.InnerText, "Views: (\\d+(,\\d+)*)");
                    if (matches.Success)
                    {
                        if (int.TryParse(matches.Groups[1].ToString(), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out int views))
                        {
                            thread.Views = views;
                        }
                    }
                }

                if (onlyParseThreads)
                {
                    // If we only want to get the thread ids for each forum, stop here.
                    continue;
                }

                // Now let's grab each thread page and it's posts.
                var threadDocument = await ScrapeThread(thread.Id);

                if (threadDocument == null)
                {
                    Console.WriteLine("Unable to fetch thread document.");
                }

                var paginator = threadDocument.DocumentNode.SelectSingleNode("//ul[@class='pagination']");
                if (paginator == default)
                {
                    // No paginator so there's only 1 page.
                    Console.WriteLine($"Only 1 page detected for '{thread.Id}'");
                    thread.PageCount = 1;
                }
                else
                {
                    try
                    {
                        var pages = paginator.SelectNodes(".//li");
                        var lastPage = pages[pages.Count - 2];

                        var lastPageParsed = int.Parse(lastPage.InnerText.Trim(Environment.NewLine.ToCharArray()));

                        thread.PageCount = lastPageParsed;
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Unable to parse last page number for thread {thread.Id}", ex);
                    }
                }

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

                    if (thread.Posts.Any(a => a.Id == post.Id))
                    {
                        // We already have this post saved (maybe from a previous cache or because it's the first post on the thread). Skip it.
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
                        post.Username = usernameBlock.InnerText.Trim();
                        var linkElem = usernameBlock.ParentNode;
                        var link = linkElem.Attributes["href"].Value;
                        if (link != "#")
                        {
                            // Not deleted.
                            var matches = Regex.Match(link, "/forums/members/(\\d*)");
                            if (matches.Success)
                            {
                                var userIdGroup = matches.Groups[1].Value;
                                if (int.TryParse(userIdGroup, out int userId))
                                {
                                    post.UserId = userId;
                                }
                            }
                        }
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

                        var thanksBoxListElem = thanksBoxElem.SelectSingleNode(".//div[@id='nguheader']");
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

                    thread.Posts.Add(post);
                }

                File.WriteAllText($"threads/{thread.Id}.json", JsonConvert.SerializeObject(thread));
            }

            return threads;
        }

        static async Task<HtmlDocument> ScrapeThread(int id)
        {
            var response = await Get($"/forums/showthread.php?t={id}");

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
            // Ty Beach for leaving the old vBulletin forum links!
            var response = await Get($"/forums/forumdisplay.php?f={id}");
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

                // Now we can begin dumping the threads and posts.


                // Get threads.. Here we go.
                var threadsElem = doc.DocumentNode.SelectNodes("//div[starts-with(@id, 'threadbit')]");

                // Set the threads ids.
                forum.ThreadsIds = (await ParseThreadListing(forum, threadsElem)).Select(a => a.Id).ToList();

                return forum;
            }
            else if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Console.WriteLine($"Forum id {id} not found");
            }

            return default;
        }

        static async Task UsersDumpTask()
        {
            if (!Directory.Exists("users"))
            {
                Directory.CreateDirectory("users");
            }

            var startUserId = 1634751; // 1512540; // 1196456;

            var path = "users.json";
            using (FileStream fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read))
            using (StreamReader reader = new StreamReader(fs))
            {
                var content = reader.ReadToEnd();
                if (content != string.Empty)
                {
                    savedUsers = new ConcurrentBag<Profile>(
                        JsonConvert.DeserializeObject<List<Profile>>(
                            content
                            ).OrderBy(a => a.UserId)
                        );
                }
            }

            if (savedUsers.Count != 0)
            {
                startUserId = savedUsers.Last().UserId;
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

        static async Task ExtractUser(int userId)
        {
            var response = await Get($"/forums/members/{userId}-username.html");

            if (response.IsSuccessStatusCode)
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(await response.Content.ReadAsStringAsync());

                var user = new Profile
                {
                    UserId = userId
                };

                // Username
                var usernameElem = doc.DocumentNode.SelectSingleNode("//span[@class='member_username']");
                if (usernameElem == default)
                {
                    return;
                }

                user.Username = usernameElem.InnerText.Trim();

                // Usertitle
                var usertitleElem = doc.DocumentNode.SelectSingleNode("//span[@class='usertitle']");
                if (usernameElem != default)
                {
                    user.Usertitle = usertitleElem.InnerText.Trim();
                }

                // Rep
                var repElem = doc.DocumentNode.SelectSingleNode("//div[@class='reputation']");
                if (repElem != default)
                {
                    var matches = Regex.Match(repElem.InnerText, "Reputation: (\\d+(,\\d+)*)");
                    if (matches.Success)
                    {
                        if (int.TryParse(matches.Groups[1].ToString(), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out int reputation))
                        {
                            user.Reputation = reputation;
                        }
                    }
                }

                // Join date
                var usermenu = doc.DocumentNode.SelectNodes("//ul[@id='usermenu']//li");
                if (usermenu != default)
                {
                    var joinDateItem = usermenu.Last();
                    var joinDateDirty = joinDateItem.InnerText.Trim(Environment.NewLine.ToCharArray()).Trim();
                    var joinDate = joinDateDirty[7..].Trim();
                    if (DateTime.TryParseExact(joinDate, "dd/MM/yy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var joinDateTime))
                    {
                        user.JoinDate = joinDateTime;
                    }
                }

                savedUsers.Add(user);

                if (savedUsers.Count % 100 == 0)
                {
                    Console.WriteLine("Saving users file.");
                    File.WriteAllText("users.json", JsonConvert.SerializeObject(savedUsers));
                }
            }
            else if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Console.WriteLine($"{userId} not found");
            }
        }

        static async Task<HttpResponseMessage> Get(string endpoint, int tries = 0, bool triggeredLongWait = false)
        {
            if (tries == 5)
            {
                Console.WriteLine("We got stuck quite a few times. Doing a long wait...");
                System.Threading.Thread.Sleep(TimeSpan.FromMinutes(5));
                return await Get(endpoint, tries++, true);
            }

            var response = await client.GetAsync(endpoint);
            if (response.StatusCode == HttpStatusCode.BadGateway)
            {
                if (triggeredLongWait)
                {
                    Console.WriteLine("No luck after waiting a long time. Let's try again later.");
                    Environment.Exit(-1);
                }

                // Banned. Let's wait.
                Console.WriteLine("Banned. Waiting 5 seconds...");
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(5));
                return await Get(endpoint, tries++);
            }
            else
            {
                return response;
            }
        }
    }
}
