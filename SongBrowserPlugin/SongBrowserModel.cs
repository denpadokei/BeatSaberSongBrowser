﻿using SongBrowserPlugin.DataAccess;
using SongBrowserPlugin.UI;
using SongLoaderPlugin;
using SongLoaderPlugin.OverrideClasses;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SongBrowserPlugin
{
    class FolderBeatMapData : BeatmapData
    {
        public FolderBeatMapData(BeatmapLineData[] beatmapLinesData, BeatmapEventData[] beatmapEventData) :
            base(beatmapLinesData, beatmapEventData)
        {
        }
    }

    class FolderBeatMapDataSO : BeatmapDataSO
    {
        public FolderBeatMapDataSO()
        {
            BeatmapLineData lineData = new BeatmapLineData();
            lineData.beatmapObjectsData = new BeatmapObjectData[0];
            this._beatmapData = new FolderBeatMapData(
                new BeatmapLineData[1]
                {
                    lineData
                },
                new BeatmapEventData[1]
                {
                    new BeatmapEventData(0, BeatmapEventType.Event0, 0)
                });
        }
    }

    class FolderLevel : StandardLevelSO
    {
        public void Init(String relativePath, String name, Sprite coverImage)
        {
            _songName = name;
            _songSubName = "";
            _songAuthorName = "Folder";

            _levelID = $"Folder_{relativePath}";

            var beatmapData = new FolderBeatMapDataSO();
            var difficultyBeatmaps = new List<CustomLevel.CustomDifficultyBeatmap>();
            var newDiffBeatmap = new CustomLevel.CustomDifficultyBeatmap(this, LevelDifficulty.Easy, 0, 0, beatmapData);
            difficultyBeatmaps.Add(newDiffBeatmap);

            var sceneInfo = Resources.Load<SceneInfo>("SceneInfo/" + "DefaultEnvironment" + "SceneInfo");
            this.InitFull(_levelID, _songName, _songSubName, _songAuthorName, SongLoaderPlugin.SongLoader.TemporaryAudioClip, 1, 1, 1, 1, 1, 1, 1, coverImage, difficultyBeatmaps.ToArray(), sceneInfo);
            this.InitData();
        }
    }

    class DirectoryNode
    {
        public string Key { get; private set; }
        public Dictionary<String, DirectoryNode> Nodes;
        public List<StandardLevelSO> Levels;

        public DirectoryNode(String key)
        {
            Key = key;
            Nodes = new Dictionary<string, DirectoryNode>();
            Levels = new List<StandardLevelSO>();
        }
    }

    public class SongBrowserModel
    {
        private const String CUSTOM_SONGS_DIR = "CustomSongs";

        private DateTime EPOCH = new DateTime(1970, 1, 1);

        private Logger _log = new Logger("SongBrowserModel");

        // song_browser_settings.xml
        private SongBrowserSettings _settings;

        // song list management
        private List<StandardLevelSO> _sortedSongs;
        private List<StandardLevelSO> _originalSongs;
        private Dictionary<String, SongLoaderPlugin.OverrideClasses.CustomLevel> _levelIdToCustomLevel;
        private SongLoaderPlugin.OverrideClasses.CustomLevelCollectionSO _gameplayModeCollection;
        private Dictionary<String, double> _cachedLastWriteTimes;
        private Dictionary<string, int> _weights;
        private Dictionary<String, DirectoryNode> _directoryTree;
        private Stack<DirectoryNode> _directoryStack = new Stack<DirectoryNode>();

        private GameplayMode _currentGamePlayMode;

        /// <summary>
        /// Toggle whether inverting the results.
        /// </summary>
        public bool InvertingResults { get; private set; }

        /// <summary>
        /// Get the settings the model is using.
        /// </summary>
        public SongBrowserSettings Settings
        {
            get
            {
                return _settings;
            }
        }

        /// <summary>
        /// Get the sorted song list for the current working directory.
        /// </summary>
        public List<StandardLevelSO> SortedSongList
        {
            get
            {
                return _sortedSongs;
            }
        }

        /// <summary>
        /// Map LevelID to Custom Level info.  
        /// </summary>
        public Dictionary<String, SongLoaderPlugin.OverrideClasses.CustomLevel> LevelIdToCustomSongInfos
        {
            get
            {
                return _levelIdToCustomLevel;
            }
        }

        /// <summary>
        /// How deep is the directory stack.
        /// </summary>
        public int DirStackSize
        {
            get
            {
                return _directoryStack.Count;
            }
        }

        /// <summary>
        /// Get the last selected (stored in settings) level id.
        /// </summary>
        public String LastSelectedLevelId
        {
            get
            {
                return _settings.currentLevelId;
            }

            set
            {
                _settings.currentLevelId = value;
                _settings.Save();
            }
        }

        /// <summary>
        /// Get the last known directory the user visited.
        /// </summary>
        public String CurrentDirectory
        {
            get
            {
                return _settings.currentDirectory;
            }

            set
            {
                _settings.currentDirectory = value;
                _settings.Save();
            }
        }

        private Playlist _currentPlaylist;

        /// <summary>
        /// Manage the current playlist if one exists.
        /// </summary>
        public Playlist CurrentPlaylist
        {
            get
            {
                if (_currentPlaylist == null)
                {
                    _currentPlaylist = PlaylistsReader.ParsePlaylist(this._settings.currentPlaylistFile);
                }

                return _currentPlaylist;
            }

            set
            {
                _settings.currentPlaylistFile = value.playlistPath;
                _currentPlaylist = value;
            }
        }


        /// <summary>
        /// Constructor.
        /// </summary>
        public SongBrowserModel()
        {
            _cachedLastWriteTimes = new Dictionary<String, double>();

            // Weights used for keeping the original songs in order
            // Invert the weights from the game so we can order by descending and make LINQ work with us...
            /*  Level4, Level2, Level9, Level5, Level10, Level6, Level7, Level1, Level3, Level8, Level11 */
            _weights = new Dictionary<string, int>
            {
                ["Level4"] = 11,
                ["Level2"] = 10,
                ["Level9"] = 9,
                ["Level5"] = 8,
                ["Level10"] = 7,
                ["Level6"] = 6,
                ["Level7"] = 5,
                ["Level1"] = 4,
                ["Level3"] = 3,
                ["Level8"] = 2,
                ["Level11"] = 1
            };
        }

        /// <summary>
        /// Init this model.
        /// </summary>
        /// <param name="songSelectionMasterView"></param>
        /// <param name="songListViewController"></param>
        public void Init()
        {
            _settings = SongBrowserSettings.Load();
            _log.Info("Settings loaded, sorting mode is: {0}", _settings.sortMode);
        }

        /// <summary>
        /// Easy invert of toggling.
        /// </summary>
        public void ToggleInverting()
        {
            this.InvertingResults = !this.InvertingResults;
        }

        /// <summary>
        /// Get the song cache from the game.
        /// TODO: This might not even be necessary anymore.  Need to test interactions with BeatSaverDownloader.
        /// </summary>
        public void UpdateSongLists(GameplayMode gameplayMode)
        {
            _currentGamePlayMode = gameplayMode;

            String customSongsPath = Path.Combine(Environment.CurrentDirectory, CUSTOM_SONGS_DIR);
            String cachedSongsPath = Path.Combine(customSongsPath, ".cache");
            DateTime currentLastWriteTIme = File.GetLastWriteTimeUtc(customSongsPath);
            IEnumerable<string> directories = Directory.EnumerateDirectories(customSongsPath, "*.*", SearchOption.AllDirectories);

            // Get LastWriteTimes            
            foreach (var level in SongLoader.CustomLevels)
            {
                //_log.Debug("Fetching LastWriteTime for {0}", slashed_dir);
                _cachedLastWriteTimes[level.levelID] = (File.GetLastWriteTimeUtc(level.customSongInfo.path) - EPOCH).TotalMilliseconds;
            }

            // Update song Infos, directory tree, and sort
            this.UpdateSongInfos(_currentGamePlayMode);
            this.UpdateDirectoryTree(customSongsPath);
            this.ProcessSongList();                       
        }

        /// <summary>
        /// Get the song infos from SongLoaderPluging
        /// </summary>
        private void UpdateSongInfos(GameplayMode gameplayMode)
        {
            _log.Trace("UpdateSongInfos for Gameplay Mode {0}", gameplayMode);

            // Get the level collection from song loader
            SongLoaderPlugin.OverrideClasses.CustomLevelCollectionsForGameplayModes collections = SongLoaderPlugin.SongLoader.Instance.GetPrivateField<SongLoaderPlugin.OverrideClasses.CustomLevelCollectionsForGameplayModes>("_customLevelCollectionsForGameplayModes");
            _gameplayModeCollection = collections.GetCollection(gameplayMode) as SongLoaderPlugin.OverrideClasses.CustomLevelCollectionSO;
            _originalSongs = collections.GetLevels(gameplayMode).ToList();
            _sortedSongs = _originalSongs;
            _levelIdToCustomLevel = new Dictionary<string, SongLoaderPlugin.OverrideClasses.CustomLevel>();
            foreach (var level in SongLoader.CustomLevels)
            {
                if (!_levelIdToCustomLevel.Keys.Contains(level.levelID))
                    _levelIdToCustomLevel.Add(level.levelID, level);
            }

            _log.Debug("Song Browser knows about {0} songs from SongLoader...", _sortedSongs.Count);
        }

        /// <summary>
        /// Make the directory tree.
        /// </summary>
        /// <param name="customSongsPath"></param>
        private void UpdateDirectoryTree(String customSongsPath)
        {
            // Determine folder mapping
            Uri customSongDirUri = new Uri(customSongsPath);
            _directoryTree = new Dictionary<string, DirectoryNode>();
            _directoryTree[CUSTOM_SONGS_DIR] = new DirectoryNode(CUSTOM_SONGS_DIR);

            if (_settings.folderSupportEnabled)
            {
                foreach (StandardLevelSO level in _originalSongs)
                {
                    AddItemToDirectoryTree(customSongDirUri, level);
                }
            }
            else
            {
                _directoryTree[CUSTOM_SONGS_DIR].Levels = _originalSongs;
            }
            
        
            // Determine starting location
            if (_directoryStack.Count < 1)
            {
                DirectoryNode currentNode = _directoryTree[CUSTOM_SONGS_DIR];
                _directoryStack.Push(currentNode);

                // Try to navigate directory path
                if (!String.IsNullOrEmpty(this.CurrentDirectory))
                {
                    String[] paths = this.CurrentDirectory.Split('/');
                    for (int i = 1; i < paths.Length; i++)
                    {
                        if (currentNode.Nodes.ContainsKey(paths[i]))
                        {
                            currentNode = currentNode.Nodes[paths[i]];
                            _directoryStack.Push(currentNode);
                        }
                    }
                }
            }

            //PrintDirectory(_directoryTree[CUSTOM_SONGS_DIR], 1);
        }

        /// <summary>
        /// Add a song to directory tree.  Determine its place in the tree by walking the split directory path.
        /// </summary>
        /// <param name="customSongDirUri"></param>
        /// <param name="level"></param>
        private void AddItemToDirectoryTree(Uri customSongDirUri, StandardLevelSO level)
        {
            //_log.Debug("Processing item into directory tree: {0}", level.levelID);
            DirectoryNode currentNode = _directoryTree[CUSTOM_SONGS_DIR];
            
            // Just add original songs to root and bail
            if (level.levelID.Length < 32)
            {
                currentNode.Levels.Add(level);
                return;
            }

            CustomSongInfo songInfo = _levelIdToCustomLevel[level.levelID].customSongInfo;            
            Uri customSongUri = new Uri(songInfo.path);
            Uri pathDiff = customSongDirUri.MakeRelativeUri(customSongUri);
            string relPath = Uri.UnescapeDataString(pathDiff.OriginalString);
            string[] paths = relPath.Split('/');
            Sprite folderIcon = Base64Sprites.Base64ToSprite(Base64Sprites.Folder);

            // Prevent cache directory from building into the tree, will add all its leafs to root.
            bool forceIntoRoot = false;
            //_log.Debug("Processing path: {0}", songInfo.path);
            if (paths.Length > 2)
            {
                forceIntoRoot = paths[1].Contains(".cache");
                Regex r = new Regex(@"^\d{1,}-\d{1,}");
                if (r.Match(paths[1]).Success)
                {
                    forceIntoRoot = true;
                }
            }

            for (int i = 1; i < paths.Length; i++)
            {
                string path = paths[i];                

                if (path == Path.GetFileName(songInfo.path))
                {
                    //_log.Debug("\tLevel Found Adding {0}->{1}", currentNode.Key, level.levelID);
                    currentNode.Levels.Add(level);
                    break;
                }
                else if (currentNode.Nodes.ContainsKey(path))
                {                    
                    currentNode = currentNode.Nodes[path];
                }
                else if (!forceIntoRoot)
                {
                    currentNode.Nodes[path] = new DirectoryNode(path);
                    FolderLevel folderLevel = new FolderLevel();
                    folderLevel.Init(relPath, path, folderIcon);

                    //_log.Debug("\tAdding folder level {0}->{1}", currentNode.Key, path);
                    currentNode.Levels.Add(folderLevel);

                    _cachedLastWriteTimes[folderLevel.levelID] = (File.GetLastWriteTimeUtc(relPath) - EPOCH).TotalMilliseconds;

                    currentNode = currentNode.Nodes[path];
                }
            }
        }

        /// <summary>
        /// Push a dir onto the stack.
        /// </summary>
        public void PushDirectory(IStandardLevel level)
        {
            DirectoryNode currentNode = _directoryStack.Peek();
            _log.Debug("Pushing directory {0}", level.songName);

            if (!currentNode.Nodes.ContainsKey(level.songName))
            {
                _log.Debug("Trying to push a directory that doesn't exist at this level.");
                return;
            }

            _directoryStack.Push(currentNode.Nodes[level.songName]);

            this.CurrentDirectory = level.levelID;
                
            ProcessSongList();            
        }

        /// <summary>
        /// Pop a dir off the stack.
        /// </summary>
        public void PopDirectory()
        {
            if (_directoryStack.Count > 1)
            {
                _directoryStack.Pop();
                String currentDir = "";
                foreach (DirectoryNode node in _directoryStack)
                {
                    currentDir = node.Key + "/" + currentDir;
                }
                this.CurrentDirectory = "Folder_" + currentDir;
                ProcessSongList();
            }      
        }

        /// <summary>
        /// Print the directory structure parsed.
        /// </summary>
        /// <param name="node"></param>
        /// <param name="depth"></param>
        private void PrintDirectory(DirectoryNode node, int depth)
        {
            Console.WriteLine("Dir: {0}".PadLeft(depth*4, ' '), node.Key);
            node.Levels.ForEach(x => Console.WriteLine("{0}".PadLeft((depth + 1)*4, ' '), x.levelID));
            foreach (KeyValuePair<string, DirectoryNode> childNode in node.Nodes)
            {
                PrintDirectory(childNode.Value, depth + 1);
            }            
        }
        
        /// <summary>
        /// Sort the song list based on the settings.
        /// </summary>
        private void ProcessSongList()
        {
            _log.Trace("ProcessSongList()");

            // This has come in handy many times for debugging issues with Newest.
            /*foreach (StandardLevelSO level in _originalSongs)
            {
                if (_levelIdToCustomLevel.ContainsKey(level.levelID))
                {
                    _log.Debug("HAS KEY {0}: {1}", _levelIdToCustomLevel[level.levelID].customSongInfo.path, level.levelID);
                }
                else
                {
                    _log.Debug("Missing KEY: {0}", level.levelID);
                }
            }*/
            
            Stopwatch stopwatch = Stopwatch.StartNew();           

            List<StandardLevelSO> songList = null;
            if (this._settings.sortMode == SongSortMode.Playlist && this.CurrentPlaylist != null)
            {                
                songList = null;
            }
            else
            {
                _log.Debug("Showing songs for directory: {0}", _directoryStack.Peek().Key);
                songList = _directoryStack.Peek().Levels;
            }
            
            switch (_settings.sortMode)
            {
                case SongSortMode.Favorites:
                    SortFavorites(songList);
                    break;
                case SongSortMode.Original:
                    SortOriginal(songList);
                    break;
                case SongSortMode.Newest:
                    SortNewest(songList);
                    break;
                case SongSortMode.Author:
                    SortAuthor(songList);
                    break;
                case SongSortMode.PlayCount:
                    SortPlayCount(songList, _currentGamePlayMode);
                    break;
                case SongSortMode.Difficulty:
                    SortDifficulty(songList);
                    break;
                case SongSortMode.Random:
                    SortRandom(songList);
                    break;
                case SongSortMode.Search:
                    SortSearch(songList);
                    break;
                case SongSortMode.Playlist:
                    SortPlaylist();
                    break;
                case SongSortMode.Default:
                default:
                    SortSongName(songList);
                    break;
            }

            if (this.InvertingResults && _settings.sortMode != SongSortMode.Random)
            {
                _sortedSongs.Reverse();
            }

            stopwatch.Stop();
            _log.Info("Sorting songs took {0}ms", stopwatch.ElapsedMilliseconds);
        }    
        
        private void SortFavorites(List<StandardLevelSO> levels)
        {
            _log.Info("Sorting song list as favorites");
            _sortedSongs = levels
                .AsQueryable()
                .OrderBy(x => _settings.favorites.Contains(x.levelID) == false)
                .ThenBy(x => x.songName)
                .ThenBy(x => x.songAuthorName)
                .ToList();
        }

        private void SortOriginal(List<StandardLevelSO> levels)
        {
            _log.Info("Sorting song list as original");
            _sortedSongs = levels
                .AsQueryable()
                .OrderByDescending(x => _weights.ContainsKey(x.levelID) ? _weights[x.levelID] : 0)
                .ThenBy(x => x.songName)
                .ToList();
        }

        private void SortNewest(List<StandardLevelSO> levels)
        {
            _log.Info("Sorting song list as newest.");
            _sortedSongs = levels
                .AsQueryable()
                .OrderBy(x => _weights.ContainsKey(x.levelID) ? _weights[x.levelID] : 0)
                .ThenByDescending(x => x.levelID.StartsWith("Level") ? _weights[x.levelID] : _cachedLastWriteTimes[x.levelID])
                .ToList();
        }

        private void SortAuthor(List<StandardLevelSO> levels)
        {
            _log.Info("Sorting song list by author");
            _sortedSongs = levels
                .AsQueryable()
                .OrderBy(x => x.songAuthorName)
                .ThenBy(x => x.songName)
                .ToList();
        }

        private void SortPlayCount(List<StandardLevelSO> levels, GameplayMode gameplayMode)
        {
            _log.Info("Sorting song list by playcount");
            // Build a map of levelId to sum of all playcounts and sort.
            PlayerDynamicData playerData = GameDataModel.instance.gameDynamicData.GetCurrentPlayerDynamicData();
            IEnumerable<LevelDifficulty> difficultyIterator = Enum.GetValues(typeof(LevelDifficulty)).Cast<LevelDifficulty>();

            Dictionary<string, int>  levelIdToPlayCount = new Dictionary<string, int>();
            foreach (var level in levels)
            {
                if (!levelIdToPlayCount.ContainsKey(level.levelID))
                {
                    // Skip folders
                    if (level.levelID.StartsWith("Folder_"))
                    {
                        levelIdToPlayCount.Add(level.levelID, 0);
                    }
                    else
                    {
                        int playCountSum = 0;
                        foreach (LevelDifficulty difficulty in difficultyIterator)
                        {
                            PlayerLevelStatsData stats = playerData.GetPlayerLevelStatsData(level.levelID, difficulty, gameplayMode);
                            playCountSum += stats.playCount;
                        }
                        levelIdToPlayCount.Add(level.levelID, playCountSum);
                    }
                }
            }

            _sortedSongs = levels
                .AsQueryable()
                .OrderByDescending(x => levelIdToPlayCount[x.levelID])
                .ThenBy(x => x.songName)
                .ToList();
        }

        private void SortDifficulty(List<StandardLevelSO> levels)
        {
            _log.Info("Sorting song list by random");

            System.Random rnd = new System.Random(Guid.NewGuid().GetHashCode());

            Dictionary<LevelDifficulty, int> difficultyWeights = new Dictionary<LevelDifficulty, int>
            {
                [LevelDifficulty.Easy] = int.MaxValue - 4,
                [LevelDifficulty.Normal] = int.MaxValue - 3,
                [LevelDifficulty.Hard] = int.MaxValue - 2,
                [LevelDifficulty.Expert] = int.MaxValue - 1,
                [LevelDifficulty.ExpertPlus] = int.MaxValue,
            };

            IEnumerable<LevelDifficulty> difficultyIterator = Enum.GetValues(typeof(LevelDifficulty)).Cast<LevelDifficulty>();
            Dictionary<string, int>  levelIdToDifficultyValue = new Dictionary<string, int>();
            foreach (var level in levels)
            {
                if (!levelIdToDifficultyValue.ContainsKey(level.levelID))
                {
                    // Skip folders
                    if (level.levelID.StartsWith("Folder_"))
                    {
                        levelIdToDifficultyValue.Add(level.levelID, 0);
                    }
                    else
                    {
                        int difficultyValue = 0;
                        foreach (LevelDifficulty difficulty in difficultyIterator)
                        {
                            IStandardLevelDifficultyBeatmap beatmap = level.GetDifficultyLevel(difficulty);
                            if (beatmap != null)
                            {
                                difficultyValue += difficultyWeights[difficulty];
                                break;
                            }
                        }
                        levelIdToDifficultyValue.Add(level.levelID, difficultyValue);
                    }
                }
            }

            _sortedSongs = levels
                .AsQueryable()
                .OrderBy(x => levelIdToDifficultyValue[x.levelID])
                .ThenBy(x => x.songName)
                .ToList();
        }

        private void SortRandom(List<StandardLevelSO> levels)
        {
            _log.Info("Sorting song list by random");

            System.Random rnd = new System.Random(Guid.NewGuid().GetHashCode());

            _sortedSongs = levels
                .AsQueryable()
                .OrderBy(x => rnd.Next())
                .ToList();
        }

        private void SortSearch(List<StandardLevelSO> levels)
        {
            // Make sure we can actually search.
            if (this._settings.searchTerms.Count <= 0)
            {
                _log.Error("Tried to search for a song with no valid search terms...");
                SortSongName(levels);
                return;
            }
            string searchTerm = this._settings.searchTerms[0];
            if (String.IsNullOrEmpty(searchTerm))
            {
                _log.Error("Empty search term entered.");
                SortSongName(levels);
                return;
            }

            _log.Info("Sorting song list by search term: {0}", searchTerm);
            //_originalSongs.ForEach(x => _log.Debug($"{x.songName} {x.songSubName} {x.songAuthorName}".ToLower().Contains(searchTerm.ToLower()).ToString()));

            _sortedSongs = levels
                .AsQueryable()
                .Where(x => $"{x.songName} {x.songSubName} {x.songAuthorName}".ToLower().Contains(searchTerm.ToLower()))
                .ToList();
            //_sortedSongs.ForEach(x => _log.Debug(x.levelID));
        }

        private void SortSongName(List<StandardLevelSO> levels)
        {
            _log.Info("Sorting song list as default (songName)");
            _sortedSongs = levels
                .AsQueryable()
                .OrderBy(x => x.songName)
                .ThenBy(x => x.songAuthorName)
                .ToList();
        }

        private void SortPlaylist()
        {
            _log.Debug("Showing songs for playlist: {0}", this.CurrentPlaylist);
            List<String> playlistNameListOrdered = this.CurrentPlaylist.songs.Select(x => x.songName).ToList();
            Dictionary<String, int> songNameToIndex = playlistNameListOrdered.Select((val, index) => new { Index = index, Value = val }).ToDictionary(i => i.Value, i => i.Index);
            HashSet<String> songNames = new HashSet<String>(playlistNameListOrdered);
            SongLoaderPlugin.OverrideClasses.CustomLevelCollectionsForGameplayModes collections = SongLoaderPlugin.SongLoader.Instance.GetPrivateField<SongLoaderPlugin.OverrideClasses.CustomLevelCollectionsForGameplayModes>("_customLevelCollectionsForGameplayModes");
            List<StandardLevelSO> songList = collections.GetLevels(_currentGamePlayMode).Where(x => songNames.Contains(x.songName)).ToList();
            _sortedSongs = songList
                .AsQueryable()
                .OrderBy(x => songNameToIndex[x.songName])
                .ToList();
        }
    }
}
