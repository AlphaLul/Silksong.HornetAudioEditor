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

[BepInAutoPlugin(id: "alphalul.HornetAudioEditor", name: "Hornet Audio Editor", version: "1.1.0")]
public partial class HornetAudioEditorPlugin : BaseUnityPlugin
{
    private Dictionary<string, List<ProbabilityAudioClip>> folderClips;
    private Dictionary<string, AudioCollection> audioCollections;
    private string audioCollectionsPath;

    private static HornetAudioEditorPlugin Instance { get; set; }
    private Harmony harmony = new(Id);
    
    private string clipsPath;
    private ConfigEntry<bool> configModEnabled;
    private ConfigEntry<bool> configLogAudio;
    private ConfigEntry<bool> configRefreshOnSaveQuit;
    
    private void Awake()
    {
        if (Instance == null) Instance = this;
        
        configModEnabled = Config.Bind(
            "General",
            "Enabled",
            true,
            "Whether the mod is active. Set to false to disable the mod.");
        configLogAudio = Config.Bind(
            "General",
            "LogAudio",
            false,
            "Whether to log the name of a RandomAudioClipTable when it plays a sound. Useful for finding table names.");
        configRefreshOnSaveQuit = Config.Bind(
            "Loading",
            "RefreshOnSaveQuit",
            true,
            "Whether to re-apply audioCollections.json upon returning to the title screen.");

        if (!configModEnabled.Value) return;
        
        audioCollectionsPath = Path.Combine(Path.GetDirectoryName(Info.Location), "audioCollections.json");
        clipsPath = Path.Combine(Path.GetDirectoryName(Info.Location), "Clips");
        Directory.CreateDirectory(clipsPath);
        
        harmony.PatchAll(typeof(GameManagerStart_Patch));
        if (configRefreshOnSaveQuit.Value)
            harmony.PatchAll(typeof(AudioTableOnEnable_Patch));
        if (configLogAudio.Value)
            harmony.PatchAll(typeof(AudioLog_Patch));

        StartCoroutine(RefreshAudioCollectionsRoutine());
    }

    private IEnumerator RefreshAudioCollectionsRoutine()
    {
        if (!RetrieveAudioCollectionsData(audioCollectionsPath))
        {
            Logger.LogError("Something is wrong with \'audioCollections.json\', unable to initialize HornetAudioEditor mod.");
            yield break;
        }
        
        yield return LoadClipsRoutine();

        RandomAudioClipTable[] loadedAudioTables = Resources.FindObjectsOfTypeAll<RandomAudioClipTable>();
        foreach (RandomAudioClipTable table in loadedAudioTables)
        {
            if (!audioCollections.TryGetValue(table.name, out AudioCollection audioCollection)) continue;
            ApplyClips(table, audioCollection);
        }
    }
    
    private IEnumerator LoadClipsRoutine()
    {
        foreach (string folder in folderClips.Keys)
        {
            List<AudioClip> streamedFolderClips = new();
            string folderPath = Path.Combine(clipsPath, folder);
            if (!Directory.Exists(folderPath)) yield break;
            
            string[] collectionWavFiles = Directory.GetFiles(folderPath, "*.wav", SearchOption.TopDirectoryOnly);
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

    private void ApplyClips(RandomAudioClipTable table, AudioCollection audioCollection)
    {
        List<ProbabilityAudioClip> clipsToApply = new();
        foreach (string folder in audioCollection.folders)
        {
            clipsToApply.AddRange(folderClips[folder]);
        }
        if (clipsToApply.Count != 0) Logger.LogInfo($"Applied mod to {table.name}");
        if (audioCollection.includeVanillaClips || clipsToApply.Count == 0)
            clipsToApply.AddRange(audioCollection.vanillaClips);
        
        table.clips = clipsToApply.ToArray();
        
    }
    
    private void LoadAudioTable(RandomAudioClipTable table)
    {
        if (audioCollections == null || folderClips == null) return;
        if (!audioCollections.TryGetValue(table.name, out AudioCollection audioCollection)) return;
        audioCollection.vanillaClips = (ProbabilityAudioClip[])table.clips.Clone();
        ApplyClips(table, audioCollection);
    }
    
    private void LogAudio(string message)
    {
        Logger.LogWarning(message);
    }

    private class AudioCollection(HashSet<string> folders, bool includeVanillaClips)
    {
        public HashSet<string> folders = folders;
        public ProbabilityAudioClip[] vanillaClips;
        public bool includeVanillaClips = includeVanillaClips;
    }

    private bool RetrieveAudioCollectionsData(string filePath)
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
                return false;
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
        folderClips = rawAudioCollectionsData.ToDictionary(kvp => kvp.Key, 
            _ => new List<ProbabilityAudioClip>());
        audioCollections = new();
            
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

        return true;
    }
    
    [HarmonyPatch(typeof(RandomAudioClipTable), "OnEnable")]
    class AudioTableOnEnable_Patch
    {
        [HarmonyPrefix]
        static void OnEnable_Prefix(RandomAudioClipTable __instance)
        {
            Instance.LoadAudioTable(__instance);
        }
    }
    
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.Start))]
    class GameManagerStart_Patch
    {
        [HarmonyPostfix]
        static void Start_Postfix()
        {
            Instance.StartCoroutine(Instance.RefreshAudioCollectionsRoutine());
        }
    }

    [HarmonyPatch(typeof(RandomAudioClipTable), nameof(RandomAudioClipTable.SelectRandomClip))]
    class AudioLog_Patch
    {
        [HarmonyPrefix]
        static void SelectRandomClip_Prefix(RandomAudioClipTable __instance)
        {
            Instance.LogAudio(__instance.name);
        }
    }
}