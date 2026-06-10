using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TL;
using WTelegram;

namespace NJMScraper
{
    class Program
    {
        private const int API_ID = 35657043;
        private const string API_HASH = "10287bf5fc1e4e752a8af08e6b480dae";
        private const string BOT_TOKEN = "8810448629:AAGJ6aNBLKJbJNxHln_LRr04sk7zV0YwmHQ";
        
        // تم تحديث الآيدي ليتوافق مع السيرفر الجديد
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

            // هنا تم تعريف جميع الأقسام مع مجلداتها الخاصة
            var categories = new List<CategoryInfo>
            {
                new CategoryInfo { TopicId = 11, FileName = "cars.json", ImageFolder = "images_cars" },
                new CategoryInfo { TopicId = 12, FileName = "maps.json", ImageFolder = "images_maps" },
                new CategoryInfo { TopicId = 13, FileName = "mods.json", ImageFolder = "images_mods" },
                new CategoryInfo { TopicId = 14, FileName = "tires.json", ImageFolder = "images_tires" },
                new CategoryInfo { TopicId = 18, FileName = "graphics.json", ImageFolder = "images_graphics" },
                new CategoryInfo { TopicId = 19, FileName = "tutorials.json", ImageFolder = "images_tutorials" },
                new CategoryInfo { TopicId = 16, FileName = "plates.json", ImageFolder = "images_plates" } 
            };

            // إنشاء المجلدات الخاصة بكل قسم إذا لم تكن موجودة
            foreach (var cat in categories)
            {
                if (!Directory.Exists(cat.ImageFolder))
                {
                    Directory.CreateDirectory(cat.ImageFolder);
                }
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
                    int m = cat.Items.Max(c => c.MessageId);
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
                var topicCurrentPhoto = new Dictionary<int, string>();
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
                    if (!topicCurrentPhoto.ContainsKey(topicId)) topicCurrentPhoto[topicId] = null;
                    if (!topicCurrentBrand.ContainsKey(topicId)) topicCurrentBrand[topicId] = 0;

                    // Handle text message OR photo caption
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
                        // Only set the name if it's not empty after removing the tag
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
                        topicCurrentPhoto[topicId] = photoFileName;
                    }
                    else if (msg.media is MessageMediaDocument docMedia && docMedia.document is Document document)
                    {
                        bool alreadyExists = categories.Any(c => c.Items.Any(item => item.MessageId == msg.id));
                        if (alreadyExists) continue;

                        var fileAttr = document.attributes.OfType<DocumentAttributeFilename>().FirstOrDefault();
                        string fileName = fileAttr?.file_name ?? "mod.zip";
                        double sizeMb = Math.Round(document.size / (1024.0 * 1024.0), 2);

                        string bName = "OTHER";
                        if (topicCurrentBrand[topicId] > 0 && topicCurrentBrand[topicId] <= 20) {
                            var brandNames = new[] { "TOYOTA", "NISSAN", "LEXUS", "GMC", "HONDA", "CHEVROLET", "KIA", "DODGE", "MAZDA", "HYUNDAI", "FORD", "BMW", "MERCEDES", "AUDI", "CHRYSLER", "CADILLAC", "LAND ROVER", "SUZUKI", "GENESIS", "OTHER" };
                            bName = brandNames[topicCurrentBrand[topicId] - 1];
                        }

                        var targetCategory = categories.FirstOrDefault(c => c.TopicId == topicId);

                        string finalImagePath = "";
                        if (topicCurrentPhoto[topicId] != null && targetCategory != null)
                        {
                            finalImagePath = $"https://raw.githubusercontent.com/NJM-2/Launcher-Data/main/{targetCategory.ImageFolder}/{topicCurrentPhoto[topicId]}";
                        }

                        var newItem = new CarMod {
                            MessageId = msg.id,
                            Name = topicCurrentName[topicId],
                            BrandId = topicCurrentBrand[topicId],
                            BrandName = bName,
                            FileInfo = $"{sizeMb} MB  •  {Path.GetExtension(fileName).ToUpper().Replace(".", "")}",
                            ImagePath = finalImagePath,
                            FileName = fileName
                        };

                        if (targetCategory != null)
                        {
                            targetCategory.Items.Add(newItem);
                            newItemsAdded++;
                            Console.WriteLine($"  [+] Added to {targetCategory.FileName}: {topicCurrentName[topicId]} ({sizeMb} MB)");
                        }
                        else
                        {
                            Console.WriteLine($"  [?] Ignored message in unknown topic ID {topicId}.");
                        }

                        topicCurrentPhoto[topicId] = null;
                        topicCurrentBrand[topicId] = 0;
                        topicCurrentName[topicId] = "ملف مجهول";
                    }
                }

                if (newItemsAdded > 0)
                {
                    SaveAllCategories(categories);
                }
            }

            Console.WriteLine("[+] Done! GitHub Actions will now commit and push the changes.");
        }

        private static void SaveAllCategories(List<CategoryInfo> categories)
        {
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            foreach (var cat in categories)
            {
                if (cat.Items.Any())
                {
                    File.WriteAllText(cat.FileName, JsonSerializer.Serialize(cat.Items, options));
                }
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
        public string FileName { get; set; }
    }
}
