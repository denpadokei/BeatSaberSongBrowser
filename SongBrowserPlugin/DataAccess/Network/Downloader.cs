﻿using SongBrowserPlugin.DataAccess.BeatSaverApi;
using SongLoaderPlugin;
using SongLoaderPlugin.OverrideClasses;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;


namespace SongBrowserPlugin
{
    class Downloader : MonoBehaviour
    {
        private Logger _log = new Logger("Downloader");

        public event Action<Song> songDownloaded;

        private static Downloader _instance = null;
        public static Downloader Instance
        {
            get
            {
                if (!_instance)
                {
                    _instance = new GameObject("SongDownloader").AddComponent<Downloader>();
                }
                return _instance;
            }
            private set
            {
                _instance = value;
            }
        }

        private List<Song> _alreadyDownloadedSongs;
        private static bool _extractingZip;

        public void Awake()
        {
            DontDestroyOnLoad(gameObject);
            if (!SongLoader.AreSongsLoaded)
            {
                SongLoader.SongsLoadedEvent += SongLoader_SongsLoadedEvent;
            }
            else
            {
                SongLoader_SongsLoadedEvent(null, SongLoader.CustomLevels);
            }
        }

        private void SongLoader_SongsLoadedEvent(SongLoader sender, List<CustomLevel> levels)
        {
            _alreadyDownloadedSongs = levels.Select(x => new Song(x)).ToList();
        }

        public IEnumerator DownloadSongCoroutine(Song songInfo)
        {
            songInfo.songQueueState = SongQueueState.Downloading;

            UnityWebRequest www;
            bool timeout = false;
            float time = 0f;
            UnityWebRequestAsyncOperation asyncRequest;

            try
            {
                www = UnityWebRequest.Get(songInfo.downloadUrl);

                asyncRequest = www.SendWebRequest();
            }
            catch (Exception e)
            {
                _log.Exception("DownloadSongCoroutine Exception", e);
                songInfo.songQueueState = SongQueueState.Error;
                songInfo.downloadingProgress = 1f;

                yield break;
            }

            while ((!asyncRequest.isDone || songInfo.downloadingProgress != 1f) && songInfo.songQueueState != SongQueueState.Error)
            {
                yield return null;

                time += Time.deltaTime;

                if ((time >= 5f && asyncRequest.progress == 0f) || songInfo.songQueueState == SongQueueState.Error)
                {
                    www.Abort();
                    timeout = true;
                    _log.Error("Connection timed out!");
                }

                songInfo.downloadingProgress = asyncRequest.progress;
            }


            if (www.isNetworkError || www.isHttpError || timeout || songInfo.songQueueState == SongQueueState.Error)
            {
                songInfo.songQueueState = SongQueueState.Error;
                _log.Error("Unable to download song! " + (www.isNetworkError ? $"Network error: {www.error}" : (www.isHttpError ? $"HTTP error: {www.error}" : "Unknown error")));
            }
            else
            {
                _log.Info("Received response from BeatSaver.com...");

                //string zipPath = "";
                string docPath = "";
                string customSongsPath = "";

                byte[] data = www.downloadHandler.data;

                Stream zipStream = null;

                try
                {
                    docPath = Application.dataPath;
                    docPath = docPath.Substring(0, docPath.Length - 5);
                    docPath = docPath.Substring(0, docPath.LastIndexOf("/"));
                    customSongsPath = docPath + "/CustomSongs/" + songInfo.id + "/";
                    //zipPath = customSongsPath + songInfo.id + ".zip";
                    if (!Directory.Exists(customSongsPath))
                    {
                        Directory.CreateDirectory(customSongsPath);
                    }
                    zipStream = new MemoryStream(data);
                    //File.WriteAllBytes(zipPath, data);
                    _log.Info("Downloaded zip!");
                }
                catch (Exception e)
                {
                    _log.Exception("DownloadSongCoroutine exception:", e);
                    songInfo.songQueueState = SongQueueState.Error;
                    yield break;
                }

                while (_extractingZip)
                {
                    yield return new WaitForSecondsRealtime(0.25f);
                }
                ExtractZipAsync(songInfo, zipStream, customSongsPath);
            }
        }

        private async void ExtractZipAsync(Song songInfo, Stream zipStream, string customSongsPath)
        {

            try
            {
                while (_extractingZip)
                {
                    Thread.Sleep(250);
                }
                _log.Info("Extracting...");
                _extractingZip = true;
                ZipArchive archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                await Task.Run(() => archive.ExtractToDirectory(customSongsPath)); //ZipFile.ExtractToDirectory(zipPath, customSongsPath));
                archive.Dispose();
                zipStream.Close();
            }
            catch (Exception e)
            {
                _log.Exception($"Unable to extract ZIP! Exception:", e);
                songInfo.songQueueState = SongQueueState.Error;
                _extractingZip = false;
                return;
            }

            songInfo.path = Directory.GetDirectories(customSongsPath).FirstOrDefault();

            if (string.IsNullOrEmpty(songInfo.path))
            {
                songInfo.path = customSongsPath;
            }

            /*
            try
            {
                await Task.Run(() => File.Delete(zipPath));
            }
            catch (IOException e)
            {
                Logger.Warning($"Unable delete zip! Exception: {e}");
                songInfo.songQueueState = SongQueueState.Error;
                _extractingZip = false;
                return;
            }*/

            _extractingZip = false;
            songInfo.songQueueState = SongQueueState.Downloaded;
            _alreadyDownloadedSongs.Add(songInfo);
            _log.Info($"Extracted {songInfo.songName} {songInfo.songSubName}!");

            songDownloaded?.Invoke(songInfo);
        }

        public bool DeleteSong(Song song)
        {
            bool zippedSong = false;
            string path = "";

            CustomLevel level = SongLoader.CustomLevels.FirstOrDefault(x => x.levelID.StartsWith(song.hash));

            if (string.IsNullOrEmpty(song.path))
            {
                if (level != null)
                    path = level.customSongInfo.path;
            }
            else
            {
                path = song.path;
            }

            if (string.IsNullOrEmpty(path))
                return false;
            if (!Directory.Exists(path))
                return false;

            if (path.Contains("/.cache/"))
                zippedSong = true;

            if (zippedSong)
            {
                _log.Info("Deleting \"" + path.Substring(path.LastIndexOf('/')) + "\"...");
                Directory.Delete(path, true);

                string songHash = Directory.GetParent(path).Name;

                try
                {
                    if (Directory.GetFileSystemEntries(path.Substring(0, path.LastIndexOf('/'))).Length == 0)
                    {
                        _log.Info("Deleting empty folder \"" + path.Substring(0, path.LastIndexOf('/')) + "\"...");
                        Directory.Delete(path.Substring(0, path.LastIndexOf('/')), false);
                    }
                }
                catch
                {
                    _log.Warning("Can't find or delete empty folder!");
                }

                string docPath = Application.dataPath;
                docPath = docPath.Substring(0, docPath.Length - 5);
                docPath = docPath.Substring(0, docPath.LastIndexOf("/"));
                string customSongsPath = docPath + "/CustomSongs/";

                string hash = "";

                foreach (string file in Directory.GetFiles(customSongsPath, "*.zip"))
                {
                    if (CreateMD5FromFile(file, out hash))
                    {
                        if (hash == songHash)
                        {
                            File.Delete(file);
                            break;
                        }
                    }
                }

            }
            else
            {
                _log.Info("Deleting \"" + path.Substring(path.LastIndexOf('/')) + "\"...");
                Directory.Delete(path, true);

                try
                {
                    if (Directory.GetFileSystemEntries(path.Substring(0, path.LastIndexOf('/'))).Length == 0)
                    {
                        _log.Info("Deleting empty folder \"" + path.Substring(0, path.LastIndexOf('/')) + "\"...");
                        Directory.Delete(path.Substring(0, path.LastIndexOf('/')), false);
                    }
                }
                catch
                {
                    _log.Warning("Unable to delete empty folder!");
                }
            }

            if (level != null)
            {
                SongLoader.Instance.RemoveSongWithLevelID(level.levelID);
            }

            _log.Info($"{_alreadyDownloadedSongs.RemoveAll(x => x.Compare(song))} song removed");
            return true;
        }

        public bool IsSongDownloaded(Song song)
        {
            if (SongLoader.AreSongsLoaded)
            {
                return _alreadyDownloadedSongs.Any(x => x.Compare(song));
            }
            else
                return false;
        }

        public static string GetLevelID(Song song)
        {
            string[] values = new string[] { song.hash, song.songName, song.songSubName, song.authorName, song.beatsPerMinute };
            return string.Join("∎", values) + "∎";
        }

        public static LevelSO GetLevel(string levelId)
        {
            return SongLoader.CustomLevelCollectionSO.levels.FirstOrDefault(x => x.levelID == levelId);
        }

        public static bool CreateMD5FromFile(string path, out string hash)
        {
            hash = "";
            if (!File.Exists(path)) return false;
            using (MD5 md5 = MD5.Create())
            {
                using (var stream = File.OpenRead(path))
                {
                    byte[] hashBytes = md5.ComputeHash(stream);

                    StringBuilder sb = new StringBuilder();
                    foreach (byte hashByte in hashBytes)
                    {
                        sb.Append(hashByte.ToString("X2"));
                    }

                    hash = sb.ToString();
                    return true;
                }
            }
        }
    }
}
