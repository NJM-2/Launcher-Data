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
        private const long CHANNEL_ID = 3701313372;
        
        // This should point to the raw GitHub URL path where images will be hosted
        private const string GITHUB_RAW_BASE = "https://raw.githubusercontent.com/NJM-2/Launcher-Data/main/images/";

        private static string Config(string what)
        {
            if (what == "api_id") return API_ID.ToString();
            if (what == "api_hash") return API_HASH;
            return null;
        }

        static async Task Main(string[] args)
        {
            Console.WriteLine("==============================================");
            Console.WriteLine("    NJM Scraper - Fetching Cars from Telegram   ");
            Console.WriteLine("==============================================");

            string carsJsonPath = "cars.json";
            string imagesDir = "images";

            if (!Directory.Exists(imagesDir))
            {
                Directory.CreateDirectory(imagesDir);
            }

            List<CarMod> cars = new List<CarMod>();

            if (File.Exists(carsJsonPath))
            {
                try
                {
                    string json = File.ReadAllText(carsJsonPath);
                    cars = JsonSerializer.Deserialize<List<CarMod>>(json) ?? new List<CarMod>();
                    Console.WriteLine($"[+] Loaded {cars.Count} cars from cars.json");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Error reading cars.json: {ex.Message}");
                }
            }

            int maxCachedId = cars.Any() ? cars.Max(c => c.MessageId) : 0;
            Console.WriteLine($"[+] Latest cached Message ID: {maxCachedId}");

            using var client = new Client(Config, new FileStream("scraper.session", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite));
            
            Console.WriteLine("[~] Connecting to Telegram...");
            await client.LoginBotIfNeeded(BOT_TOKEN);
            Console.WriteLine("[+] Connected successfully!");

            var resolvedChannels = await client.Channels_GetChannels(new InputChannel(CHANNEL_ID, 0));
            var actualChannel = resolvedChannels.chats.Values.OfType<Channel>().FirstOrDefault(c => c.id == CHANNEL_ID);

            if (actualChannel == null)
            {
                Console.WriteLine("[!] Error: Channel not found.");
                return;
            }

            if (cars.Any())
            {
                Console.WriteLine("[~] Checking for deleted cars...");
                var existingIds = cars.Select(c => c.MessageId).ToList();
                var deletedIds = new List<int>();
                bool madeChanges = false;

                for (int i = 0; i < existingIds.Count; i += 100)
                {
                    var chunk = existingIds.Skip(i).Take(100).ToList();
                    var inputIds = chunk.Select(id => new InputMessageID { id = id }).ToArray<InputMessage>();
                    
                    try
                    {
                        var res = await client.Channels_GetMessages(actualChannel, inputIds);
                        
                        // Collect all valid IDs returned by Telegram
                        var validIds = res.Messages
                                          .Where(m => !(m is MessageEmpty))
                                          .Select(m => m.ID)
                                          .ToHashSet();

                        // If an ID we requested is NOT in the valid returned IDs, it was deleted!
                        foreach (var reqId in chunk)
                        {
                            if (!validIds.Contains(reqId))
                            {
                                deletedIds.Add(reqId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Error checking deleted messages: {ex.Message}");
                    }
                    await Task.Delay(1000);
                }

                if (deletedIds.Any())
                {
                    int removedCount = cars.RemoveAll(c => deletedIds.Contains(c.MessageId));
                    Console.WriteLine($"[-] Removed {removedCount} deleted cars from the list.");
                    madeChanges = true;
                }
                
                if (madeChanges)
                {
                    var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                    File.WriteAllText(carsJsonPath, JsonSerializer.Serialize(cars, options));
                }
            }

            Console.WriteLine("[~] Fetching new messages...");
            
            var messages = new List<Message>();
            int offsetId = 0;
            
            while (true)
            {
                try
                {
                    var history = await client.Messages_GetHistory(actualChannel, offset_id: offsetId, limit: 100);
                    if (history.Messages.Length == 0) break;
                    
                    bool reachedCached = false;
                    foreach (var msgBase in history.Messages)
                    {
                        if (msgBase.ID <= maxCachedId)
                        {
                            reachedCached = true;
                            break;
                        }
                        if (msgBase is Message msg)
                        {
                            messages.Add(msg);
                        }
                    }
                    
                    if (reachedCached) break;
                    
                    offsetId = history.Messages.Last().ID;
                    
                    // If we only need updates, 100 messages (1 chunk) is usually enough unless he posted >30 cars at once.
                    if (maxCachedId > 0 && messages.Count >= 200) break; 
                    
                    await Task.Delay(2000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Error fetching history: {ex.Message}");
                    break;
                }
            }

            // The history comes newest-to-oldest. We MUST reverse it to oldest-to-newest
            // so that our text -> photo -> zip matching logic works correctly!
            messages.Reverse();
            
            if (!messages.Any())
            {
                Console.WriteLine("[+] No new messages to process.");
            }
            else
            {
                Console.WriteLine($"[~] Processing {messages.Count} new messages...");
                
                string currentName = "سيارة مجهولة";
                string currentPhotoFileName = null;
                int currentBrandId = 0;
                int newCarsAdded = 0;

                foreach (var msg in messages)
                {
                    if (!string.IsNullOrWhiteSpace(msg.message) && msg.media == null)
                    {
                        string msgText = msg.message;
                        currentBrandId = 0;
                        var match = System.Text.RegularExpressions.Regex.Match(msgText, @"#(\d+)|(\d+)#");
                        if (match.Success)
                        {
                            string numStr = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                            if (int.TryParse(numStr, out int id) && id >= 1 && id <= 20)
                            {
                                currentBrandId = id;
                                msgText = msgText.Replace(match.Value, "");
                            }
                        }
                        currentName = msgText.Replace("*", "").Replace("=", "").Trim();
                    }
                    else if (msg.media is MessageMediaPhoto photoMedia && photoMedia.photo is Photo photo)
                    {
                        string photoFileName = $"thumb_{msg.id}.jpg";
                        string photoPath = Path.Combine(imagesDir, photoFileName);
                        if (!File.Exists(photoPath))
                        {
                            Console.WriteLine($"  [~] Downloading image {photoFileName}...");
                            try {
                                using var fs = File.Create(photoPath);
                                await client.DownloadFileAsync(photo, fs);
                            } catch { }
                        }
                        currentPhotoFileName = photoFileName;
                    }
                    else if (msg.media is MessageMediaDocument docMedia && docMedia.document is Document document)
                    {
                        // Check if we already have this car
                        if (cars.Any(c => c.MessageId == msg.id)) continue;

                        var fileAttr = document.attributes.OfType<DocumentAttributeFilename>().FirstOrDefault();
                        string fileName = fileAttr?.file_name ?? "mod.zip";
                        double sizeMb = Math.Round(document.size / (1024.0 * 1024.0), 2);
                        
                        string bName = "OTHER";
                        if (currentBrandId > 0 && currentBrandId <= 20) {
                            var brandNames = new[] { "TOYOTA", "NISSAN", "LEXUS", "GMC", "HONDA", "CHEVROLET", "KIA", "DODGE", "MAZDA", "HYUNDAI", "FORD", "BMW", "MERCEDES", "AUDI", "CHRYSLER", "CADILLAC", "LAND ROVER", "SUZUKI", "GENESIS", "OTHER" };
                            bName = brandNames[currentBrandId - 1];
                        }

                        string finalImagePath = currentPhotoFileName != null ? GITHUB_RAW_BASE + currentPhotoFileName : "";

                        var car = new CarMod {
                            MessageId = msg.id,
                            Name = currentName,
                            BrandId = currentBrandId,
                            BrandName = bName,
                            FileInfo = $"{sizeMb} MB  •  ZIP",
                            ImagePath = finalImagePath,
                            FileName = fileName
                        };

                        cars.Add(car);
                        newCarsAdded++;
                        Console.WriteLine($"  [+] Added Car: {currentName} ({sizeMb} MB)");

                        currentPhotoFileName = null;
                        currentBrandId = 0;
                        currentName = "سيارة مجهولة";
                    }
                }

                if (newCarsAdded > 0)
                {
                    Console.WriteLine($"[+] Saving {cars.Count} total cars to cars.json...");
                    var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                    File.WriteAllText(carsJsonPath, JsonSerializer.Serialize(cars, options));
                }
            }

            Console.WriteLine("[+] Done! You can now commit and push the changes to GitHub.");
            Console.WriteLine("[+] Done! GitHub Actions will now commit and push the changes.");
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
