using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;
using ProbabilityAudioClip = RandomAudioClipTable.ProbabilityAudioClip;

namespace HornetAudioEditor;
#nullable disable

[BepInAutoPlugin(id: "alphalul.HornetAudioEditor", name: "Hornet Audio Editor", version: "1.0.0")]
public partial class HornetAudioEditorPlugin : BaseUnityPlugin
{
    private AudioCollectionsData audioCollectionsData;
    private Dictionary<string, List<ProbabilityAudioClip>> folderClips;
    private Dictionary<string, AudioCollection> audioCollections;
    private string audioCollectionsPath;

    public static HornetAudioEditorPlugin Instance { get; private set; }
    private Harmony harmony = new(Id);
    
    private string clipsPath;
    private ConfigEntry<bool> configModEnabled;
    private ConfigEntry<bool> configLogAudio;
    private ConfigEntry<bool> configRefreshOnSaveQuit;
    private ConfigEntry<KeyCode> configRefreshHotkey;
    
    private void Awake()
    {
        if (Instance == null) Instance = this;
        
        configModEnabled = Config.Bind(
            "General",
            "Enabled",
            true,
            "Whether or not to execute the mod. Set to false to disable the mod.");
        configLogAudio = Config.Bind(
            "General",
            "LogAudio",
            false,
            "Whether or not to log the name of a RandomAudioClipTable whenever it plays a sound. " + 
            "Useful for finding the names of audio tables.");
        configRefreshOnSaveQuit = Config.Bind(
            "Loading",
            "RefreshOnSaveQuit",
            true,
            "Whether or not to refresh the mod after returning to the title screen.");
        configRefreshHotkey = Config.Bind(
            "Loading",
            "RefreshHotkey",
            KeyCode.None,
            "An optional hotkey to refresh the mod any time in-game. " +
            "Can be used to load RandomAudioClipTables that aren't available on launch when pressed.");

        if (!configModEnabled.Value) return;
        
        audioCollectionsPath = Path.Combine(Path.GetDirectoryName(Info.Location), "audioCollections.json");
        clipsPath = Path.Combine(Path.GetDirectoryName(Info.Location), "Clips");
        Directory.CreateDirectory(clipsPath);
        
        harmony.PatchAll(typeof(GameManagerStart_Patch));
        if (configLogAudio.Value)
            harmony.PatchAll(typeof(AudioLog_Patch));
    }

    private void Update()
    {
        if (configRefreshHotkey.Value != KeyCode.None && Input.GetKeyDown(configRefreshHotkey.Value))
        {
            ExecuteHornetAudioEditor();
        }
    }

    public void ExecuteHornetAudioEditor()
    {
        if (!configModEnabled.Value) return;
        if (audioCollectionsData != null && !configRefreshOnSaveQuit.Value) return;
        StartCoroutine(ExecuteHornetAudioEditorRoutine());
    }

    private IEnumerator ExecuteHornetAudioEditorRoutine()
    {
        ResetClips();
        audioCollectionsData = AudioCollectionsData.RetrieveAudioCollectionsData(audioCollectionsPath);
        folderClips = audioCollectionsData?.folderClips;
        audioCollections = audioCollectionsData?.audioCollections;
        
        if (audioCollectionsData == null || folderClips == null || audioCollections == null)
        {
            Logger.LogError(
                $"Something is wrong with \'audioCollections.json\', unable to initialize HornetAudioEditor mod.");
            yield break;
        }
        foreach (string subfolder in folderClips.Keys)
        {
            if (Directory.Exists(Path.Combine(clipsPath, subfolder))) continue;
            
            Logger.LogWarning($"\'{subfolder}\' folder does not exist. Creating new folder");
            Directory.CreateDirectory(Path.Combine(clipsPath, subfolder));
        }
        
        CacheTables();
        yield return LoadClipsRoutine();
        ApplyClips();
    }
    
    private void ResetClips()
    {
        if (audioCollections == null) return;
        foreach (AudioCollection audioCollection in audioCollections.Values)
        {
            if (audioCollection.vanillaClips.IsNullOrEmpty()) continue;
            audioCollection.table.clips = audioCollection.vanillaClips;
        }
    }
    
    private IEnumerator LoadClipsRoutine()
    {
        foreach (string folder in folderClips.Keys)
        {
            List<AudioClip> streamedFolderClips = new();
            string folderPath = Path.Combine(clipsPath, folder);
            string[] collectionWavFiles = Directory.GetFiles(folderPath, "*.wav", SearchOption.TopDirectoryOnly);
            
            if (collectionWavFiles.Length == 0)
            {
                Logger.LogWarning($"No wav files found in \'Clips{(folder.Equals("") ? "" : "/")}{folder}\' folder");
            }
            
            foreach (string wavFile in collectionWavFiles)
                yield return WavToAudioClipRoutine(wavFile, streamedFolderClips);
            
            // Convert AudioClips to ProbabilityAudioClips
            foreach (AudioClip clip in streamedFolderClips)
            {
                folderClips[folder].Add(new ProbabilityAudioClip
                {
                    Clip = clip,
                    Probability = 1f
                });
            }
        }
    }
    
    private IEnumerator WavToAudioClipRoutine(string wavFile, List<AudioClip> clips)
    {
        string uri = new Uri(wavFile).AbsoluteUri;
        using UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.WAV);
        yield return req.SendWebRequest();
            
        AudioClip streamedClip = DownloadHandlerAudioClip.GetContent(req);
        streamedClip.name = Path.GetFileNameWithoutExtension(wavFile);
        streamedClip.LoadAudioData();

        clips.Add(streamedClip);
    }

    private void CacheTables()
    {
        IEnumerable<AssetBundle> assetBundles = AssetBundle.GetAllLoadedAssetBundles();
        
        List<string> tablesToRemove = new();
        foreach ((string tableName, AudioCollection audioCollection) in audioCollections)
        {
            audioCollection.table = FindAndLoadTable(tableName, assetBundles);
            if (!audioCollection.table)
            {
                Logger.LogError($"Unable to find RandomAudioClipTable named \'{tableName}\'");
                tablesToRemove.Add(tableName);
                continue;
            }
            
            audioCollection.vanillaClips = (ProbabilityAudioClip[])audioCollection.table.clips.Clone();
        }
        foreach (string tableName in tablesToRemove)
        {
            audioCollections.Remove(tableName);
        }
    }

    private RandomAudioClipTable FindAndLoadTable(string tableName, IEnumerable<AssetBundle> assetBundles)
    {
        foreach (AssetBundle assetBundle in assetBundles)
        {
            string tablePath = assetBundle.GetAllAssetNames().FirstOrDefault(assetPath => Path.GetFileName(assetPath).Equals($"{tableName}.asset"));
            if (tablePath == null) continue;
            
            RandomAudioClipTable table = assetBundle.LoadAsset<RandomAudioClipTable>(tablePath);
            if (table) return table;
        }

        return null;
    }
    
    private void ApplyClips()
    {
        foreach ((string tableName, AudioCollection audioCollection) in audioCollections)
        {
            List<ProbabilityAudioClip> clipsToApply = new();
            foreach (string folder in audioCollection.folders)
            {
                clipsToApply.AddRange(folderClips[folder]);
            }
            if (clipsToApply.Count > 0)
                Logger.LogInfo($"Applied {clipsToApply.Count} modded clips to \'{tableName}\'");

            if (audioCollection.includeVanillaClips || clipsToApply.Count == 0)
            {
                clipsToApply.AddRange(audioCollection.vanillaClips);
                Logger.LogInfo($"Applied {audioCollection.vanillaClips.Length} vanilla clips to \'{tableName}\'");
            }
            
            audioCollection.table.clips = clipsToApply.ToArray();
        }
    }

    public void LogAudio(string message)
    {
        Logger.LogWarning(message);
    }
}

public class AudioCollection(HashSet<string> folders, bool includeVanillaClips)
{
    public HashSet<string> folders = folders;
    public RandomAudioClipTable table;
    public ProbabilityAudioClip[] vanillaClips;
    public bool includeVanillaClips = includeVanillaClips;
}

public class AudioCollectionsData
{
    public Dictionary<string, List<ProbabilityAudioClip>> folderClips;
    public Dictionary<string, AudioCollection> audioCollections;
    
    public static AudioCollectionsData RetrieveAudioCollectionsData(string filePath)
    {
        Dictionary<string, string[]> rawAudioCollectionsData;
        JsonSerializerSettings jsonSettings = new()
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore
        };
        
        if (File.Exists(filePath))
        {
            try
            {
                rawAudioCollectionsData = JsonConvert.DeserializeObject<Dictionary<string, string[]>>(File.ReadAllText(filePath));
            }
            catch (Exception exception)
            {
                Debug.LogError($"Hornet Audio Editor: {exception.Message}");
                return null;
            }
        }
        else
        {
            rawAudioCollectionsData = new Dictionary<string, string[]>
            {
                [""] = 
                [
                    "Taunt Hornet Voice",
                    "Taunt Seriously Hornet Voice",
                    "Hornet_poshanka"
                ]
            };
        }
        
        string json = JsonConvert.SerializeObject(rawAudioCollectionsData, jsonSettings);
        File.WriteAllText(filePath, json);
        
        // Parse raw data into list of folders/clips and list of audio collections
        Dictionary<string, List<ProbabilityAudioClip>> folderClips = rawAudioCollectionsData.ToDictionary(kvp => kvp.Key, 
            kvp => new List<ProbabilityAudioClip>());
        Dictionary<string, AudioCollection> audioCollections = new();
        foreach ((string folderName, string[] tableNames) in rawAudioCollectionsData)
        {
            foreach (string tableName in tableNames)
            {
                string trimmedTableName = tableName.TrimStart('+');
                if (audioCollections.ContainsKey(trimmedTableName))
                {
                    AudioCollection entry = audioCollections[trimmedTableName];
                    entry.folders.Add(folderName);
                    entry.includeVanillaClips |= tableName.StartsWith('+');
                    audioCollections[trimmedTableName] = entry;
                }
                else
                {
                    audioCollections[trimmedTableName] = new AudioCollection([folderName], tableName.StartsWith('+'));
                }
            }
        }
        
        return new AudioCollectionsData {folderClips = folderClips, audioCollections = audioCollections};
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.Start))]
class GameManagerStart_Patch
{
    [HarmonyPostfix]
    static void Start_Postfix(GameManager __instance)
    {
        HornetAudioEditorPlugin.Instance.ExecuteHornetAudioEditor();
    }
}

[HarmonyPatch(typeof(RandomAudioClipTable), nameof(RandomAudioClipTable.SelectRandomClip))]
class AudioLog_Patch
{
    [HarmonyPrefix]
    static void SelectRandomClip_Prefix(RandomAudioClipTable __instance)
    {
        HornetAudioEditorPlugin.Instance.LogAudio(__instance.name);
    }
}