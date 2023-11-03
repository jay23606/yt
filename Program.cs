using YoutubeExplode;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;
using NAudio.Wave;
using NAudio.Lame;
using System.Diagnostics;
using HtmlAgilityPack;
using System.Text.RegularExpressions;

namespace yt
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Provide a txt file with format like ARTIST <TAB> SONG NAME and the MP3s will be downloaded from YouTube (optional 2nd argument for destination folder name)");
                return;
            }
            string _artist = "";
            //search wiki for the song names if not provided a text file
            if (!args[0].ToLower().EndsWith(".txt") && args.Length > 1)
            {
                var songs = FindSongs(args[0], args[1]);
                int idx = 1;
                foreach (var song in songs)
                {
                    string[] parts = song.Split('\t');
                    if (parts.Length == 2)
                    {
                        string artist = parts[0];
                        _artist = artist;
                        string song_ = parts[1];
                        Console.WriteLine($"Downloading Artist: {artist}, Song: {song_}");
                        await DownloadSong(song, artist, idx);
                        idx++;
                    }
                }
            }
            else
            {
                using (StreamReader reader = new StreamReader(args[0]))
                {
                    string line;
                    int idx = 1;
                    while ((line = reader.ReadLine()!) != null)
                    {
                        string[] parts = line.Split('\t');
                        if (parts.Length == 2)
                        {
                            string artist = parts[0];
                            _artist = artist;
                            string song = parts[1];
                            Console.WriteLine($"Downloading Artist: {artist}, Song: {song}");
                            await DownloadSong(song, artist, idx);
                            idx++;
                        }
                    }
                }
            }

            //allows you to specify the artist name for the folder it gets put into
            if (args.Length>1) _artist = args[1];

            Console.WriteLine($"Burning CD for {_artist}. This may take a while!");
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = @"C:\burn\cdbxpcmd";
            startInfo.Arguments = $@"--burn-audio -folder:""{Path.Combine(Directory.GetCurrentDirectory(), _artist)}""";
            using (Process process = new Process())
            {
                process.StartInfo = startInfo;
                process.Start();
                process.WaitForExit();
                int exitCode = process.ExitCode;
                Console.WriteLine("Process exited with code: " + exitCode);
            }

        }

        public static bool IsArtistFound(Video? video, string artist)
        {
            if (video == null) return false;
            return video.Title.ToString().ToLower().Contains(artist.ToLower()) || video.Description.ToLower().Contains(artist.ToLower());
        }

        public async static Task DownloadSong(string song, string artist, int index)
        {
            var videoId = GetYouTubeVideoId(song, artist);
            if (videoId == null || videoId.Length < 5)
            {
                Console.WriteLine($"Could not find video for {song} by {artist}");
                return;
            }
            var youtube = new YoutubeClient();
            var video = await youtube.Videos.GetAsync(videoId);

            //Check that we have the right artist, sometimes it hallucinates and gives us the wrong artist
            //but it could be happening further down the line
            if (!IsArtistFound(video, artist))
            {
                Console.WriteLine($"Could not find video for {song} by {artist}");
                int retryCount = 0;
                while (retryCount < 3)
                {
                    await Task.Delay(10000);
                    videoId = GetYouTubeVideoId(artist, song);
                    if (videoId == null || videoId.Length < 5)
                    {
                        Console.WriteLine($"Could not find video for {song} by {artist}");
                        return;
                    }
                    video = await youtube.Videos.GetAsync(videoId);
                    if (IsArtistFound(video, artist)) break;
                    retryCount++;
                }
                if (retryCount >= 3)
                {
                    Console.WriteLine($"Could not download song {song} by {artist} even after retrying {retryCount} times");
                    return;
                }
            }

            var streamManifest = await youtube.Videos.Streams.GetManifestAsync(videoId);
            var audioStreamInfo = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();
            var artistDirectory = Path.Combine(Environment.CurrentDirectory, artist);
            if (!Directory.Exists(artistDirectory)) Directory.CreateDirectory(artistDirectory);
            var outputFilePath = Path.Combine(artistDirectory, $"{new string(video.Title.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray())}.{audioStreamInfo.Container}");
            await youtube.Videos.Streams.DownloadAsync(audioStreamInfo, outputFilePath);
            var directoryPath = Path.GetDirectoryName(outputFilePath);
            int leftPad = (index < 100) ? 2 : ((index < 1000) ? 3 : ((index < 10000) ? 4 : 5));
            var fileName = $"{index.ToString().PadLeft(leftPad, '0')}. " + Path.GetFileNameWithoutExtension(outputFilePath) + ".mp3";
            var mp3OutputFilePath = Path.Combine(directoryPath!, fileName);
            using (var reader = new MediaFoundationReader(outputFilePath))
                using (var outputFile = new LameMP3FileWriter(mp3OutputFilePath, reader.WaveFormat, LAMEPreset.STANDARD)) reader.CopyTo(outputFile);
            
            File.Delete(outputFilePath);
        }


        static string GetYouTubeVideoId(string songTitle, string artistName)
        {
            string searchQuery = $"{artistName} {songTitle}";
            using (var client = new HttpClient())
            {
                var searchUrl = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(searchQuery)}";
                var response = client.GetAsync(searchUrl).Result;
                var responseContent = response.Content.ReadAsStringAsync().Result;
                var videoId = ExtractVideoIdFromJson(responseContent);
                return videoId;
            }
        }

        static string ExtractVideoIdFromJson(string json)
        {
            int startIndex = json.IndexOf("\"videoId\":\"") + "\"videoId\":\"".Length;
            int endIndex = json.IndexOf("\"", startIndex);
            var videoId = json.Substring(startIndex, endIndex - startIndex);
            return videoId;
        }

        static HashSet<string> FindSongs(string album, string artist)
        {
            HashSet<string> artistSong = new();
            using (var client = new HttpClient())
            {
                string basePath = "https://en.wikipedia.org/";
                string searchUrl = $"{basePath}/w/index.php?search={Uri.EscapeDataString(album + " " + artist)}&title=Special%3ASearch&ns0=1";
                var response = client.GetAsync(searchUrl).Result;
                var wikiSearchResponse = response.Content.ReadAsStringAsync().Result;
                //search for class="mw-search-result-heading" and extract the href from the first anchor tag
                var classTag = "class=\"mw-search-result-heading\"><a href=\"";
                int startIndex = wikiSearchResponse.IndexOf(classTag) + classTag.Length;
                int endIndex = wikiSearchResponse.IndexOf("\"", startIndex);
                var href = wikiSearchResponse.Substring(startIndex, endIndex - startIndex);

                string albumUrl = basePath + href;
                response = client.GetAsync(albumUrl).Result;
                var albumUrlResponse = response.Content.ReadAsStringAsync().Result;

                // Find id="track1" through id="track99" and extract innerText from the next TD element
                //for (int i = 1; i <= 99; i++)
                //{
                //    string trackId = "track" + i;
                //    string pattern = $"id=\"{trackId}\".*?<td.*?>(.*?)</td>";

                //    var match = Regex.Match(albumUrlResponse, pattern, RegexOptions.Singleline);
                //    if (match.Success)
                //    {
                //        string innerText = match.Groups[1].Value;
                //        innerText = Regex.Replace(innerText, "<.*?>", string.Empty);
                //        var arr = innerText.Replace("\"", "").Split("\n");
                //        foreach (var line in arr) artistSong.Add(string.Join("\t", artist, line));
                //    }
                //    else break;
                //}
                int lastIndex = 0;
                string remainingInput = albumUrlResponse;
                for (; ; ) 
                {
                    string trackId = "track";
                    //string pattern = $"id=\"{trackId}\".*?<td.*?>(.*?)</td>";
                    string pattern = $"id=\"{trackId}\\d*\".*?<td.*?>(.*?)</td>";

                    var match = Regex.Match(remainingInput, pattern, RegexOptions.Singleline);
                    if (match.Success) 
                    {
                        lastIndex = match.Index + match.Length; 
                        string innerText = match.Groups[1].Value;
                        innerText = Regex.Replace(innerText, "<.*?>", string.Empty);
                        var arr = innerText.Replace("\"", "").Split("\n");
                        foreach (var line in arr)
                        {
                            var artistLine = string.Join("\t", artist, line);
                            if (!artistSong.Contains(artistLine)) artistSong.Add(artistLine);
                        }
                    }
                    else break;
                    remainingInput = remainingInput.Substring(lastIndex);
                }
            }
            return artistSong;
        }
    }
}


