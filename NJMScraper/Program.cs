using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
using TL;
using WTelegram;

namespace NJMScraper
{
    class Program
    {
        private const int API_ID = 35657043;
        private const string API_HASH = "10287bf5fc1e4e752a8af08e6b480dae";
        private const string BOT_TOKEN = "8810448629:AAGJ6aNBLKJbJNxHln_LRr04sk7zV0YwmHQ";

        private const long CHANNEL_ID = 4297697800; 

        private static string Config(string what)
        {
            if (what == "api_id") return API_ID.ToString();
            if (what == "api_hash") return API_HASH;
            return null;
        }

        class CategoryInfo
        {
            public int TopicId { get; set; }
            public string FileName { get; set; }
            public string ImageFolder { get; set; }
            public List<CarMod> Items { get; set; } = new List<CarMod>();
        }

        static async Task Main(string[] args)
        {
            Console.WriteLine("==============================================");
            Console.WriteLine("    NJM Scraper - Fetching Mods from Telegram ");
            Console.WriteLine("==============================================");

            var categories = new List<CategoryInfo>
            {
                new CategoryInfo { TopicId = 11, FileName = "cars.json", ImageFolder = "images_cars" },
                new CategoryInfo { TopicId = 12, FileName = "maps.json", ImageFolder = "images_maps" },
                new CategoryInfo { TopicId = 13, FileName = "mods.json", ImageFolder = "images_mods" },
                new CategoryInfo { TopicId = 14, FileName = "tires.json", ImageFolder = "images_tires" },
                new CategoryInfo { TopicId = 18, FileName = "graphics.json", ImageFolder = "images_graphics" },
                new CategoryInfo { TopicId = 19, FileName = "tutorials.json", ImageFolder = "images_tutorials" },
                new CategoryInfo { TopicId = 16, FileName = "plates.json", ImageFolder = "images_plates" },
                new CategoryInfo { TopicId = 1108, FileName = "sport_cars.json", ImageFolder = "images_sport_cars" },
                new CategoryInfo { TopicId = 1123, FileName = "trucks.json", ImageFolder = "images_trucks" }
            };

            foreach (var cat in categories)
            {
                if (!Directory.Exists(cat.ImageFolder)) Directory.CreateDirectory(cat.ImageFolder);
            }

            foreach (var cat in categories)
            {
                if (File.Exists(cat.FileName))
                {
                    try
                    {
                        string json = File.ReadAllText(cat.FileName);
                        cat.Items = JsonSerializer.Deserialize<List<CarMod>>(json) ?? new List<CarMod>();
                        Console.WriteLine($"[+] Loaded {cat.Items.Count} items from {cat.FileName}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Error reading {cat.FileName}: {ex.Message}");
                    }
                }
            }

            int maxCachedId = 0;
            foreach (var cat in categories)
            {
                if (cat.Items.Any())
                {
                    int m = cat.Items.Max(c => Math.Max(c.MessageId, c.VideoMessageId));
                    if (m > maxCachedId) maxCachedId = m;
                }
            }
            Console.WriteLine($"[+] Latest cached Message ID overall: {maxCachedId}");

            using var client = new Client(Config, new FileStream("scraper.session", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite));

            Console.WriteLine("[~] Connecting to Telegram...");
            await client.LoginBotIfNeeded(BOT_TOKEN);
            Console.WriteLine("[+] Connected successfully!");

            var resolvedChannels = await client.Channels_GetChannels(new InputChannel(CHANNEL_ID, 0));
            var actualChannel = resolvedChannels.chats.Values.OfType<Channel>().FirstOrDefault(c => c.id == CHANNEL_ID);

            if (actualChannel == null)
            {
                Console.WriteLine("[!] Error: Channel not found. Make sure Bot is in the group and Admin.");
                return;
            }

            Console.WriteLine("[~] Checking for deleted messages...");
            bool anyDeletions = false;
            foreach (var cat in categories)
            {
                if (!cat.Items.Any()) continue;

                var existingIds = cat.Items.Select(c => c.MessageId).ToList();
                var deletedIds = new List<int>();

                for (int i = 0; i < existingIds.Count; i += 100)
                {
                    var chunk = existingIds.Skip(i).Take(100).ToList();
                    var inputIds = chunk.Select(id => new InputMessageID { id = id }).ToArray<InputMessage>();

                    try
                    {
                        var res = await client.Channels_GetMessages(actualChannel, inputIds);
                        var validIds = res.Messages.Where(m => !(m is MessageEmpty)).Select(m => m.ID).ToHashSet();
                        foreach (var reqId in chunk)
                        {
                            if (!validIds.Contains(reqId)) deletedIds.Add(reqId);
                        }
                    }
                    catch (TL.RpcException ex) when (ex.Code == 420)
                    {
                        Console.WriteLine($"[!] Flood wait for {ex.X} seconds. Waiting...");
                        await Task.Delay((ex.X + 1) * 1000);
                        i -= 100;
                    }
                    catch { }
                    await Task.Delay(2000);
                }

                if (deletedIds.Any())
                {
                    int removedCount = cat.Items.RemoveAll(c => deletedIds.Contains(c.MessageId));
                    Console.WriteLine($"[-] Removed {removedCount} deleted items from {cat.FileName}.");
                    anyDeletions = true;
                }
            }

            if (anyDeletions) SaveAllCategories(categories);

            Console.WriteLine("[~] Fetching new messages...");

            var messages = new List<Message>();
            int currentId = (maxCachedId > 0) ? maxCachedId + 1 : 1;
            int maxEmptyChunks = 5;
            int emptyChunksCount = 0;

            while (emptyChunksCount < maxEmptyChunks)
            {
                var chunkIds = new List<InputMessage>();
                for (int i = currentId; i < currentId + 100; i++)
                {
                    chunkIds.Add(new InputMessageID { id = i });
                }

                try
                {
                    var res = await client.Channels_GetMessages(actualChannel, chunkIds.ToArray());
                    var validMsgs = res.Messages.OfType<Message>().ToList();

                    if (validMsgs.Any())
                    {
                        messages.AddRange(validMsgs);
                        emptyChunksCount = 0;
                        Console.WriteLine($"[+] Fetched {validMsgs.Count} new messages.");
                    }
                    else
                    {
                        emptyChunksCount++;
                    }
                }
                catch (TL.RpcException ex) when (ex.Code == 420)
                {
                    Console.WriteLine($"[!] Flood wait for {ex.X} seconds. Waiting...");
                    await Task.Delay((ex.X + 1) * 1000);
                    continue;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Chunk error: {ex.Message}");
                    break;
                }

                currentId += 100;
                await Task.Delay(2000);
            }

            messages = messages.OrderBy(m => m.id).ToList();

            if (!messages.Any())
            {
                Console.WriteLine("[+] No new messages to process.");
            }
            else
            {
                Console.WriteLine($"[~] Processing {messages.Count} new messages...");

                var topicCurrentName = new Dictionary<int, string>();
                var topicCurrentPhotos = new Dictionary<int, List<string>>();
                var topicCurrentVideoMessageId = new Dictionary<int, int>();
                var topicCurrentBrand = new Dictionary<int, int>();
                int newItemsAdded = 0;

                foreach (var msg in messages)
                {
                    int topicId = 0;

                    if (msg.reply_to is MessageReplyHeader replyHeader)
                    {
                        topicId = replyHeader.reply_to_top_id != 0 ? replyHeader.reply_to_top_id : replyHeader.reply_to_msg_id;
                    }
                    if (topicId == 0) topicId = -1;

                    if (!topicCurrentName.ContainsKey(topicId)) topicCurrentName[topicId] = "ملف مجهول";
                    if (!topicCurrentPhotos.ContainsKey(topicId)) topicCurrentPhotos[topicId] = new List<string>();
                    if (!topicCurrentVideoMessageId.ContainsKey(topicId)) topicCurrentVideoMessageId[topicId] = 0;
                    if (!topicCurrentBrand.ContainsKey(topicId)) topicCurrentBrand[topicId] = 0;

                    if (!string.IsNullOrWhiteSpace(msg.message))
                    {
                        string msgText = msg.message;
                        var match = System.Text.RegularExpressions.Regex.Match(msgText, @"#(\d+)|(\d+)#");
                        if (match.Success)
                        {
                            string numStr = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                            if (int.TryParse(numStr, out int id) && id >= 1 && id <= 20)
                            {
                                topicCurrentBrand[topicId] = id;
                                msgText = msgText.Replace(match.Value, "");
                            }
                        }
                        string parsedName = msgText.Replace("*", "").Replace("=", "").Trim();
                        if (!string.IsNullOrEmpty(parsedName))
                        {
                            topicCurrentName[topicId] = parsedName;
                        }
                    }

                    if (msg.media is MessageMediaPhoto photoMedia && photoMedia.photo is Photo photo)
                    {
                        string photoFileName = $"thumb_{msg.id}.jpg";
                        var targetCategory = categories.FirstOrDefault(c => c.TopicId == topicId);
                        string targetImageFolder = targetCategory?.ImageFolder ?? "images_other";

                        if (!Directory.Exists(targetImageFolder))
                        {
                            Directory.CreateDirectory(targetImageFolder);
                        }

                        string photoPath = Path.Combine(targetImageFolder, photoFileName);
                        if (!File.Exists(photoPath))
                        {
                            Console.WriteLine($"  [~] Downloading image {photoFileName} to {targetImageFolder}...");
                            try {
                                using var fs = File.Create(photoPath);
                                await client.DownloadFileAsync(photo, fs);
                            } catch { }
                        }
                        topicCurrentPhotos[topicId].Add(photoFileName);

                        if (topicId == 19)
                        {
                            var targetCat = categories.FirstOrDefault(c => c.TopicId == topicId);
                            if (targetCat != null && !targetCat.Items.Any(item => item.MessageId == msg.id))
                            {
                                string tutorialImagePath = "";
                                var tutorialImagePaths = new List<string>();
                                if (topicCurrentPhotos[topicId].Any())
                                {
                                    tutorialImagePath = $"https://raw.githubusercontent.com/NJM-2/Launcher-Data/main/{targetCat.ImageFolder}/{topicCurrentPhotos[topicId].Last()}";
                                    foreach (var p in topicCurrentPhotos[topicId])
                                    {
                                        tutorialImagePaths.Add($"https://raw.githubusercontent.com/NJM-2/Launcher-Data/main/{targetCat.ImageFolder}/{p}");
                                    }
                                }

                                var carMod = new CarMod
                                {
                                    Name = topicCurrentName[topicId],
                                    MessageId = msg.id,
                                    FileName = "",
                                    VideoMessageId = 0,
                                    BrandId = topicCurrentBrand[topicId],
                                    ImagePath = tutorialImagePath,
                                    ImagePaths = tutorialImagePaths
                                };
                                targetCat.Items.Add(carMod);
                                newItemsAdded++;

                                topicCurrentName[topicId] = "ملف مجهول";
                                topicCurrentPhotos[topicId].Clear();
                                topicCurrentBrand[topicId] = 0;
                            }
                        }
                    }
                    else if (msg.media is MessageMediaDocument docMedia && docMedia.document is Document document)
                    {
                        bool isVideo = document.attributes.Any(a => a is DocumentAttributeVideo || a is DocumentAttributeAnimated);
                        if (isVideo)
                        {
                            string photoFileName = $"thumb_{msg.id}.jpg";
                            var videoTargetCategory = categories.FirstOrDefault(c => c.TopicId == topicId);
                            string targetImageFolder = videoTargetCategory?.ImageFolder ?? "images_other";

                            if (!Directory.Exists(targetImageFolder))
                            {
                                Directory.CreateDirectory(targetImageFolder);
                            }

                            string photoPath = Path.Combine(targetImageFolder, photoFileName);
                            if (!File.Exists(photoPath))
                            {
                                Console.WriteLine($"  [~] Downloading video thumbnail {photoFileName} to {targetImageFolder}...");
                                try {
                                    var thumb = document.thumbs?.OfType<PhotoSize>().LastOrDefault();
                                    if (thumb != null)
                                    {
                                        using var fs = File.Create(photoPath);
                                        await client.DownloadFileAsync(document, fs, thumb);
                                    }
                                } catch { }
                            }
                            topicCurrentPhotos[topicId].Add(photoFileName);
                            topicCurrentVideoMessageId[topicId] = msg.id;

                            if (topicId == 19)
                            {
                                var targetCat = categories.FirstOrDefault(c => c.TopicId == topicId);
                                if (targetCat != null && !targetCat.Items.Any(item => item.MessageId == msg.id))
                                {
                                    string tutorialImagePath = "";
                                    var tutorialImagePaths = new List<string>();
                                    if (topicCurrentPhotos[topicId].Any())
                                    {
                                        tutorialImagePath = $"https://raw.githubusercontent.com/NJM-2/Launcher-Data/main/{targetCat.ImageFolder}/{topicCurrentPhotos[topicId].Last()}";
                                        foreach (var p in topicCurrentPhotos[topicId])
                                        {
                                            tutorialImagePaths.Add($"https://raw.githubusercontent.com/NJM-2/Launcher-Data/main/{targetCat.ImageFolder}/{p}");
                                        }
                                    }

                                    var carMod = new CarMod
                                    {
                                        Name = topicCurrentName[topicId],
                                        MessageId = msg.id,
                                        FileName = "",
                                        VideoMessageId = topicCurrentVideoMessageId[topicId],
                                        BrandId = topicCurrentBrand[topicId],
                                        ImagePath = tutorialImagePath,
                                        ImagePaths = tutorialImagePaths
                                    };
                                    targetCat.Items.Add(carMod);
                                    newItemsAdded++;

                                    topicCurrentName[topicId] = "ملف مجهول";
                                    topicCurrentPhotos[topicId].Clear();
                                    topicCurrentVideoMessageId[topicId] = 0;
                                    topicCurrentBrand[topicId] = 0;
                                }
                            }
                            continue;
                        }

                        bool alreadyExists = categories.Any(c => c.Items.Any(item => item.MessageId == msg.id));
                        if (alreadyExists) continue;

                        var fileAttr = document.attributes.OfType<DocumentAttributeFilename>().FirstOrDefault();
                        string fileName = fileAttr?.file_name ?? "mod.zip";
                        double sizeMb = Math.Round(document.size / (1024.0 * 1024.0), 2);

                        string bName = "OTHER";
                        if (topicId == 1108 && topicCurrentBrand[topicId] > 0 && topicCurrentBrand[topicId] <= 18)
                        {
                            var sportBrandNames = new[] { "BMW", "MERCEDES", "FERRARI", "LAMBORGHINI", "LAND ROVER", "PORSCHE", "BENTLEY", "ROLLS ROYCE", "AUDI", "ALFA ROMEO", "BUGATTI", "KOENIGSEGG", "DODGE", "CHEVROLET", "CADILLAC", "NISSAN", "TOYOTA", "OTHER" };
                            bName = sportBrandNames[topicCurrentBrand[topicId] - 1];
                        }
                        else if (topicCurrentBrand[topicId] > 0 && topicCurrentBrand[topicId] <= 20) 
                        {
                            var brandNames = new[] { "TOYOTA", "NISSAN", "LEXUS", "GMC", "HONDA", "CHEVROLET", "KIA", "DODGE", "MAZDA", "HYUNDAI", "FORD", "BMW", "MERCEDES", "AUDI", "CHRYSLER", "CADILLAC", "LAND ROVER", "SUZUKI", "GENESIS", "OTHER" };
                            bName = brandNames[topicCurrentBrand[topicId] - 1];
                        }

                        var targetCategory = categories.FirstOrDefault(c => c.TopicId == topicId);

                        string finalImagePath = "";
                        var finalImagePaths = new List<string>();

                        if (targetCategory != null && topicCurrentPhotos[topicId].Any())
                        {
                            finalImagePath = $"https://raw.githubusercontent.com/NJM-2/Launcher-Data/main/{targetCategory.ImageFolder}/{topicCurrentPhotos[topicId].Last()}";
                            foreach (var p in topicCurrentPhotos[topicId])
                            {
                                finalImagePaths.Add($"https://raw.githubusercontent.com/NJM-2/Launcher-Data/main/{targetCategory.ImageFolder}/{p}");
                            }
                        }

                        var newItem = new CarMod {
                            MessageId = msg.id,
                            Name = topicCurrentName[topicId],
                            BrandId = topicCurrentBrand[topicId],
                            BrandName = bName,
                            FileInfo = $"{sizeMb} MB  •  {Path.GetExtension(fileName).ToUpper().Replace(".", "")}",
                            ImagePath = finalImagePath,
                            ImagePaths = finalImagePaths,
                            VideoMessageId = topicCurrentVideoMessageId[topicId],
                            FileName = fileName
                        };

                        if (targetCategory != null)
                        {
                            targetCategory.Items.Add(newItem);
                            newItemsAdded++;
                            Console.WriteLine($"  [+] Added to {targetCategory.FileName}: {topicCurrentName[topicId]} ({sizeMb} MB)");
                        }

                        topicCurrentPhotos[topicId].Clear();
                        topicCurrentVideoMessageId[topicId] = 0;
                        topicCurrentBrand[topicId] = 0;
                        topicCurrentName[topicId] = "ملف مجهول";
                    }
                }

                if (newItemsAdded > 0)
                {
                    SaveAllCategories(categories);
                }
            }
            
            // ==========================================
            // GITHUB RELEASES AUTO-UPLOAD (THE MAGIC!)
            // ==========================================
            Console.WriteLine("[~] Checking for items missing GitHub Download URLs...");
            string githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            
            if (string.IsNullOrEmpty(githubToken))
            {
                Console.WriteLine("[!] GITHUB_TOKEN is not set. Skipping GitHub upload phase.");
            }
            else
            {
                using var httpClient = new HttpClient();
                httpClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                httpClient.DefaultRequestHeaders.Add("User-Agent", "NJMScraper");
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {githubToken}");
                httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

                string releaseUploadUrl = "";
                var releaseResponse = await httpClient.GetAsync("https://api.github.com/repos/NJM-2/Launcher-Data/releases/tags/mods-storage");
                if (releaseResponse.IsSuccessStatusCode)
                {
                    var json = await releaseResponse.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    releaseUploadUrl = doc.RootElement.GetProperty("upload_url").GetString().Split('{')[0];
                    Console.WriteLine("[+] Found existing GitHub Release 'mods-storage'.");
                }
                else
                {
                    Console.WriteLine("[~] Creating new GitHub Release 'mods-storage'...");
                    var createPayload = new { tag_name = "mods-storage", name = "Mods Storage", body = "Automated storage for launcher mods and videos." };
                    var createResponse = await httpClient.PostAsync("https://api.github.com/repos/NJM-2/Launcher-Data/releases", new StringContent(JsonSerializer.Serialize(createPayload), Encoding.UTF8, "application/json"));
                    if (createResponse.IsSuccessStatusCode)
                    {
                        var json = await createResponse.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        releaseUploadUrl = doc.RootElement.GetProperty("upload_url").GetString().Split('{')[0];
                        Console.WriteLine("[+] Created new GitHub Release successfully.");
                    }
                    else
                    {
                        Console.WriteLine("[!] Failed to create GitHub Release: " + await createResponse.Content.ReadAsStringAsync());
                    }
                }

                if (!string.IsNullOrEmpty(releaseUploadUrl))
                {
                    bool anyUploads = false;
                    foreach (var cat in categories)
                    {
                        foreach (var item in cat.Items)
                        {
                            if (string.IsNullOrEmpty(item.DownloadUrl))
                            {
                                int targetMsgId = item.MessageId;
                                
                                // إذا كان العنصر من الشروحات ولديه مقطع فيديو
                                if (cat.TopicId == 19 && item.VideoMessageId > 0)
                                {
                                    targetMsgId = item.VideoMessageId;
                                }

                                if (targetMsgId <= 0) continue;

                                Console.WriteLine($"[~] Process Item '{item.Name}' (MsgId: {targetMsgId})...");

                                var msgs = await client.Channels_GetMessages(actualChannel, new[] { new InputMessageID { id = targetMsgId } });
                                var msg = msgs.Messages.OfType<Message>().FirstOrDefault();
                                
                                if (msg?.media is MessageMediaDocument docMedia && docMedia.document is Document doc)
                                {
                                    string ext = ".zip";
                                    if (cat.TopicId == 19) ext = ".mp4";
                                    
                                    string tempFile = Path.Combine(Path.GetTempPath(), $"{targetMsgId}{ext}");
                                    
                                    Console.WriteLine($"    -> Downloading from Telegram...");
                                    try 
                                    {
                                        using (var fs = File.Create(tempFile))
                                        {
                                            await client.DownloadFileAsync(doc, fs);
                                        }
                                        
                                        Console.WriteLine($"    -> Uploading to GitHub Releases...");
                                        string safeName = string.IsNullOrWhiteSpace(item.FileName) ? $"mod{ext}" : item.FileName;
                                        string assetName = $"{targetMsgId}_{safeName.Replace(" ", "_")}";
                                        
                                        string uploadUrl = $"{releaseUploadUrl}?name={Uri.EscapeDataString(assetName)}";
                                        
                                        using var fileStream = File.OpenRead(tempFile);
                                        var content = new StreamContent(fileStream);
                                        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                                        
                                        var uploadResponse = await httpClient.PostAsync(uploadUrl, content);
                                        if (uploadResponse.IsSuccessStatusCode)
                                        {
                                            var json = await uploadResponse.Content.ReadAsStringAsync();
                                            using var jsonDoc = JsonDocument.Parse(json);
                                            item.DownloadUrl = jsonDoc.RootElement.GetProperty("browser_download_url").GetString();
                                            Console.WriteLine($"    -> Success! URL: {item.DownloadUrl}");
                                            anyUploads = true;
                                        }
                                        else
                                        {
                                            string err = await uploadResponse.Content.ReadAsStringAsync();
                                            if (err.Contains("already_exists"))
                                            {
                                                Console.WriteLine($"    -> Asset already exists! Recovering existing URL...");
                                                item.DownloadUrl = $"https://github.com/NJM-2/Launcher-Data/releases/download/mods-storage/{Uri.EscapeDataString(assetName)}";
                                                Console.WriteLine($"    -> Recovered URL: {item.DownloadUrl}");
                                                anyUploads = true;
                                            }
                                            else
                                            {
                                                Console.WriteLine($"    -> Upload failed: {err}");
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"    -> Error: {ex.Message}");
                                    }
                                    finally
                                    {
                                        if (File.Exists(tempFile)) File.Delete(tempFile);
                                    }
                                }
                            }
                        }
                    }

                    if (anyUploads)
                    {
                        SaveAllCategories(categories);
                    }
                }
            }

            Console.WriteLine("[+] Done! GitHub Actions will now commit and push the changes.");
        }

        private static void SaveAllCategories(List<CategoryInfo> categories)
        {
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            foreach (var cat in categories)
            {
                File.WriteAllText(cat.FileName, JsonSerializer.Serialize(cat.Items, options));
            }
        }
    }

    public class CarMod
    {
        public int MessageId { get; set; }
        public string Name { get; set; }
        public int BrandId { get; set; }
        public string BrandName { get; set; }
        public string FileInfo { get; set; }
        public string ImagePath { get; set; }
        public List<string> ImagePaths { get; set; }
        public int VideoMessageId { get; set; }
        public string FileName { get; set; }
        public string DownloadUrl { get; set; }
    }
}
