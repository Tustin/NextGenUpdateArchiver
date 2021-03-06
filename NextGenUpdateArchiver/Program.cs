﻿using HtmlAgilityPack;
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
using System.Linq.Expressions;
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
        static List<Forum> forums = new List<Forum>();

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

            string cookieName = Environment.GetEnvironmentVariable("NGU_SESSION_NAME");
            string cookieValue = Environment.GetEnvironmentVariable("NGU_SESSION_VALUE");

            if (File.Exists("cookies.json"))
            {
                var cookies = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText("cookies.json"));
                cookieName = cookies[0];
                cookieValue = cookies[1];

                Console.WriteLine("Loaded from cookies.json");
            }

            try
            {
                cookieContainer.Add(baseAddress, new Cookie(
                    cookieName, cookieValue
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
                    await ThreadsDumpTask();
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

        static async Task ThreadsDumpTask()
        {
            var forumHome = "https://www.nextgenupdate.com/forums/";


            if (!Directory.Exists("threads"))
            {
                Directory.CreateDirectory("threads");
            }

            if (File.Exists("forums.json"))
            {
                forums = JsonConvert.DeserializeObject<List<Forum>>(File.ReadAllText("forums.json"));
            }


            if (forums.Count == 0)
            {
                // We need to dump all the forums first.
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

            Console.WriteLine($"Scraping {forums.Count} forums...");

            foreach (var forum in forums)
            {
                var flattened = FlattenForums(forum);
                foreach (var f in flattened)
                {
                    await ParseThreadListing(f);
                }
            }

            // Now that we have all the forums, we can now dump the threads.
            // Get threads.. Here we go.
            //var threadsElem = doc.DocumentNode.SelectNodes("//div[starts-with(@id, 'threadbit')]");

            //// Set the threads ids.
            //forum.ThreadsIds = (await ParseThreadListing(forum, threadsElem)).Select(a => a.Id).ToList();
        }

        public static IEnumerable<Forum> FlattenForums(Forum forum)
        {
            if (forum == null)
            {
                yield break;
            }

            yield return forum;
            foreach (var f in forum.SubForums)
            {
                foreach (var subforum in FlattenForums(f))
                {
                    yield return subforum;
                }
            }
        }

        static async Task ParseThreadListing(Forum forum)
        {
            int page = 1;

            do
            {
                var response = await Get($"{forum.Slug}/index{page}.html");

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[err] Failed getting page {page} for {forum.Slug}");
                    return;
                }

                var doc = new HtmlDocument();
                doc.LoadHtml(await response.Content.ReadAsStringAsync());

                var threadsElem = doc.DocumentNode.SelectNodes("//div[starts-with(@id, 'threadbit')]");

                if (threadsElem == default)
                {
                    Console.WriteLine($"No threads in forum '{forum.Name}'");
                    continue;
                }

                Console.WriteLine($"Getting threads in forum '{forum.Name}' (page {page}/{forum.PageCount})");

                foreach (var t in threadsElem)
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

                    if (forum.ThreadsIds.Contains(thread.Id))
                    {
                        // Already saved thread.
                        continue;
                    }

                    // Lets try to grab the thread creation date to see if we even need to parse it.
                    var threadInfoLeft = t.SelectSingleNode("//div[@class='thread_info_left']");
                    if (threadInfoLeft != default)
                    {
                        try
                        {
                            var info = threadInfoLeft.InnerText.Replace("\n", "").Replace("\r", "");
                            // This sucks.
                            var date = info[(info.IndexOf(",") + 1)..info.IndexOf(".")];
                            if (DateTime.TryParse(date, out var time))
                            {
                                if (time <= new DateTime(2014, 02, 01))
                                {
                                    // Old thread, dont bother.
                                    continue;
                                }
                            }
                        }
                        catch
                        {
                            // This will almost certainly fail at some point so just ignore it and let us dump the thread anyways.
                        }
                    }

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

                    // Now let's grab each thread page and it's posts.
                    var threadDocument = await ScrapeThread(thread.Id);

                    if (threadDocument == null)
                    {
                        Console.WriteLine("Unable to fetch thread document.");
                        continue;
                    }

                    var paginator = threadDocument.DocumentNode.SelectSingleNode("//ul[@class='pagination']");
                    if (paginator == default)
                    {
                        // No paginator so there's only 1 page.
                        thread.PageCount = 1;
                    }
                    else
                    {
                        try
                        {
                            var pages = paginator.SelectNodes(".//li");
                            var lastPage = pages[^2];

                            var lastPageParsed = int.Parse(lastPage.InnerText.Trim(Environment.NewLine.ToCharArray()));

                            thread.PageCount = lastPageParsed;
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"Unable to parse last page number for thread {thread.Id}", ex);
                        }
                    }

                    try
                    {
                        Console.WriteLine($"\t Parsing thread '{thread.Title}' ({thread.PageCount} pages)");
                        await ParsePosts(forum, thread);
                        File.WriteAllText($"threads/{thread.Id}.json", JsonConvert.SerializeObject(thread));
                        if (!forum.ThreadsIds.Contains(thread.Id))
                        {
                            forum.ThreadsIds.Add(thread.Id);
                        }
                        File.WriteAllText("forums.json", JsonConvert.SerializeObject(forums));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[err] Failed fetching thread", ex.Message);
                    }

                }
            } while (page++ < forum.PageCount);
        }

        static int GetPostId(HtmlNode p)
        {
            var pid = p.Attributes["id"].Value;

            if (Regex.IsMatch(pid, "^post_\\d*$"))
            {
                try
                {
                    if (!int.TryParse(pid[("post_".Length)..], out var pidOnly))
                    {
                        throw new Exception($"Unable to parse post id integer from {pid}");
                    }

                    return pidOnly;
                }
                catch (Exception ex)
                {
                    throw new Exception($"Unable to parse post id {pid}", ex);
                }
            }

            return 0;
        }

        static async Task ParsePosts(Forum forum, NGUThread thread)
        {
            int page = 1;

            do
            {
                var response = await Get($"{forum.Slug}/{thread.Id}-aa-{page}.html");

                if (response.IsSuccessStatusCode)
                {
                    var threadDocument = new HtmlDocument();
                    threadDocument.LoadHtml(await response.Content.ReadAsStringAsync());

                    // Get all posts.
                    var posts = threadDocument.DocumentNode.SelectNodes("//div[starts-with(@id, 'post_')]");

                    if (posts == default)
                    {
                        throw new Exception("Failed finding posts in thread");
                    }

                    Console.WriteLine($"\t\tGetting posts for thread '{thread.Title}' (page {page}/{thread.PageCount})");

                    foreach (var p in posts)
                    {
                        try
                        {
                            var postId = GetPostId(p);

                            if (postId == 0)
                            {
                                continue;
                            }

                            if (thread.Posts.Any(a => a.Id == postId))
                            {
                                // We already have this post saved (maybe from a previous cache or because it's the first post on the thread). Skip it.
                                continue;
                            }

                            // If the post needs thanked before we dump it, do that now.
                            if (PostNeedsThanked(p))
                            {
                                Console.WriteLine("\t\t - Has hidden contents. Thanking post...");
                                var csrfElem = p.SelectSingleNode(".//input[@name='_token']");

                                if (csrfElem != default)
                                {
                                    var csrf = csrfElem.Attributes["value"].Value;
                                    var formContent = new FormUrlEncodedContent(new[]
                                    {
                                    new KeyValuePair<string, string>("_token", csrf),
                                });

                                    var thankResponse = await Post($"{forum.Slug}/{postId}/thank", formContent);
                                    if (thankResponse.StatusCode == HttpStatusCode.OK)
                                    {
                                        await ParsePosts(forum, thread);
                                        return;
                                    }
                                    else
                                    {
                                        Console.WriteLine($"\t\t[err] Failed thanking post {postId}. Continuing without thanking it...");
                                    }
                                }
                                else
                                {
                                    Console.WriteLine($"\t\t[err] Post {postId} needs thanked but couldn't find CSRF token.");
                                }
                            }

                            var post = new Post()
                            {
                                Id = postId
                            };

                            // Post contents (HTML for now...)
                            var postContentElem = p.SelectSingleNode(".//div[@class='postcontent']");
                            if (postContentElem == default)
                            {
                                throw new Exception("No post content found for post");
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

                            // Try to get thanks.
                            var thanksBoxElem = threadDocument.DocumentNode.SelectSingleNode($"//li[@id='post_thanks_box_{post.Id}']");
                            if (thanksBoxElem != default)
                            {
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
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[err] Failed parsing post: {ex.Message}");
                        }
                    }
                }
            } while (page++ < thread.PageCount);
        }

        private static bool PostNeedsThanked(HtmlNode p)
        {
            var postContentElem = p.SelectSingleNode(".//div[@class='postcontent']");
            if (postContentElem == default)
            {
                throw new Exception("No post content found for post");
            }
            else
            {
                var needsThanked = postContentElem.SelectSingleNode(".//span[@class='label label-primary']");

                // Holy shit Beach is so dumb that any quoted post that has hidden content will ALWAYS be hidden even if the original post was thanked...
                // Beach if you ever read this you are truly retarded.
                if (needsThanked != default)
                {
                    // We only want to try to thank the post if it does not contain a quote...
                    // Probably a few small false positives but it's the the risk to bypass Beach's stupidity.
                    var quoteNode = postContentElem.SelectSingleNode(".//div[@class='jb_quote_container']");

                    return quoteNode == default;
                }

                return false;
            }
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

        static async Task<Forum> ScrapeForumListing(int id)
        {
            // Ty Beach for leaving the old vBulletin forum links!
            var response = await Get($"/forums/forumdisplay.php?f={id}");
            if (response.IsSuccessStatusCode)
            {
                var path = response.RequestMessage.RequestUri.AbsolutePath;

                var forum = new Forum(id)
                {
                    Slug = path
                };

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

            var startUserId = 1653277; // 1512540; // 1196456;

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
                startUserId = savedUsers.First().UserId;
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

        static async Task<HttpResponseMessage> Post(string endpoint, HttpContent content, int tries = 0, bool triggeredLongWait = false)
        {
            if (tries == 5)
            {
                Console.WriteLine("We got stuck quite a few times. Doing a long wait...");
                System.Threading.Thread.Sleep(TimeSpan.FromMinutes(5));
                return await Post(endpoint, content, tries++, true);
            }

            var response = await client.PostAsync(endpoint, content);
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
                return await Post(endpoint, content, tries++);
            }
            else
            {
                return response;
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
