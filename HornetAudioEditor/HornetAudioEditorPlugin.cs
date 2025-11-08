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

[BepInAutoPlugin(id: "alphalul.HornetAudioEditor", name: "Hornet Audio Editor", version: "1.2.1")]
public partial class HornetAudioEditorPlugin : BaseUnityPlugin
{
    private Dictionary<string, List<ProbabilityAudioClip>> folderClips;
    private Dictionary<string, AudioCollection> audioCollections;
    private string clipsPath;
    private string audioCollectionsPath;
    private string collectionPresetsPath;
    
    private static JsonSerializerSettings jsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        DefaultValueHandling = DefaultValueHandling.Ignore
    };

    private static HornetAudioEditorPlugin Instance { get; set; }
    private Harmony harmony = new(Id);
    
    private ConfigEntry<bool> configModEnabled;
    private ConfigEntry<bool> configRefreshOnSaveQuit;
    private ConfigEntry<bool> configLogAudio;
    private ConfigEntry<float> configLogSpamCooldown;

    private HashSet<string> recentLogs = new();
    private Queue<(string log, float time)> logBufferQueue = new();
    
    private void Awake()
    {
        if (Instance == null) Instance = this;
        
        configModEnabled = Config.Bind(
            "General",
            "Enabled",
            true,
            "Whether the mod is active. Set to false to disable modification of audio.");
        configRefreshOnSaveQuit = Config.Bind(
            "General",
            "RefreshOnSaveQuit",
            true,
            "Whether to reapply audioCollections.json upon returning to the title screen.");
        configLogAudio = Config.Bind(
            "Logging",
            "LogAudio",
            false,
            "Whether to log the name of a RandomAudioClipTable when it plays a sound. Useful for finding table names.");
        configLogSpamCooldown = Config.Bind(
            "Logging",
            "LogSpamCooldown",
            0.2f,
            "How many seconds to wait until logging the same RandomAudioClipTable again. Helps reduce console spam.");
       

        //Should log audio even if other functionality is disabled
        if (configLogAudio.Value)
            harmony.PatchAll(typeof(AudioLog_Patch));
        if (!configModEnabled.Value) return;
        
        clipsPath = Path.Combine(Path.GetDirectoryName(Info.Location), "Clips");
        audioCollectionsPath = Path.Combine(Path.GetDirectoryName(Info.Location), "audioCollections.json");
        collectionPresetsPath = Path.Combine(Path.GetDirectoryName(Info.Location), "Collection Presets");
        Directory.CreateDirectory(clipsPath);
        
        harmony.PatchAll(typeof(AudioTableOnEnable_Patch));
        if (configRefreshOnSaveQuit.Value)
            harmony.PatchAll(typeof(Start_Patch));
    }
    
    private IEnumerator RefreshAudioCollectionsRoutine()
    {
        RandomAudioClipTable[] loadedAudioTables = Resources.FindObjectsOfTypeAll<RandomAudioClipTable>();
        ResetPersistentTables(loadedAudioTables);
        
        if (!RetrieveAudioCollectionsData())
        {
            Logger.LogError("Something is wrong with \'audioCollections.json\', unable to initialize HornetAudioEditor mod.");
            yield break;
        }
        
        //Load clips
        foreach (string folder in folderClips.Keys)
        {
            List<AudioClip> streamedFolderClips = new();
            string folderPath = Path.Combine(clipsPath, folder);
            if (!Directory.Exists(folderPath))
            {
                Logger.LogWarning($"Folder {folder} doesn't exist");
                continue;
            }
            
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

        foreach (RandomAudioClipTable table in loadedAudioTables)
        {
            LoadAudioTable(table);
        }
    }

    private void ResetPersistentTables(RandomAudioClipTable[] loadedAudioTables)
    {
        if (audioCollections == null || folderClips == null) return;
        foreach (RandomAudioClipTable table in loadedAudioTables)
        {
            if (!audioCollections.TryGetValue(table.name, out AudioCollection audioCollection)) continue;
            table.clips = audioCollection.vanillaClips;
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
    
    private void LoadAudioTable(RandomAudioClipTable table)
    {
        if (audioCollections == null || folderClips == null) return;
        if (!audioCollections.TryGetValue(table.name, out AudioCollection audioCollection)) return;
        
        audioCollection.vanillaClips = (ProbabilityAudioClip[])table.clips.Clone();
        
        List<ProbabilityAudioClip> clipsToApply = new();
        foreach (string folder in audioCollection.folders)
        {
            clipsToApply.AddRange(folderClips[folder]);
        }
        if (clipsToApply.Count == 0) return;
        
        if (audioCollection.includeVanillaClips)
        {
            Logger.LogInfo($"Applied {clipsToApply.Count} modded clip(s) and {table.clips.Length} vanilla clip(s) to {table.name}");
            clipsToApply.AddRange(table.clips);
        }
        else
            Logger.LogInfo($"Applied {clipsToApply.Count} modded clip(s) to {table.name}");
        
        table.clips = clipsToApply.ToArray();
    }

    private class AudioCollection(HashSet<string> folders, bool includeVanillaClips)
    {
        public HashSet<string> folders = folders;
        public ProbabilityAudioClip[] vanillaClips;
        public bool includeVanillaClips = includeVanillaClips;
    }

    private bool RetrieveAudioCollectionsData()
    {
        Dictionary<string, List<string>> rawAudioCollectionsData;
        
        if (File.Exists(audioCollectionsPath))
        {
            try
            {
                rawAudioCollectionsData = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(File.ReadAllText(audioCollectionsPath), jsonSettings);
            }
            catch (Exception exception)
            {
                Debug.LogError($"Hornet Audio Editor: {exception.Message}");
                return false;
            }
        }
        else
        {
            rawAudioCollectionsData = new Dictionary<string, List<string>>
            {
                [""] = 
                [
                    "Taunt Hornet Voice",
                    "Taunt Seriously Hornet Voice",
                    "Hornet_poshanka"
                ]
            };
            
            string json = JsonConvert.SerializeObject(rawAudioCollectionsData, jsonSettings);
            File.WriteAllText(audioCollectionsPath, json);
        }
        
        ApplyCollectionPresets(rawAudioCollectionsData);
        ParseRawAudioCollectionsData(rawAudioCollectionsData);
        return true;
    }

    private void ApplyCollectionPresets(Dictionary<string, List<string>> rawAudioCollectionsData)
    {
        Dictionary<string, string[]> presetCache = new();
        
        foreach (List<string> tablesList in rawAudioCollectionsData.Values)
        {
            foreach (string preset in tablesList.Where(t => t.EndsWith(".json")).ToArray())
            {
                try
                {
                    bool includeVanillaClips = preset.StartsWith('+');
                    string presetKey = preset.TrimStart('+');
                    
                    if (!presetCache.TryGetValue(presetKey, out string[] tables))
                    {
                        tables = JsonConvert.DeserializeObject<string[]>(
                            File.ReadAllText(Path.Combine(collectionPresetsPath, presetKey)), jsonSettings);
                        presetCache[presetKey] = tables;
                    }

                    if (includeVanillaClips)
                    {
                        for (int i = 0; i < tables.Length; i++)
                        {
                            if (!tables[i].StartsWith('+'))
                                tables[i] = $"+{tables[i]}";
                        }
                    }
                    tablesList.AddRange(tables);
                }
                catch (Exception exception)
                {
                    Debug.LogError($"Hornet Audio Editor: {exception.Message}");
                }
                tablesList.Remove(preset);
            }
        }
    }

    private void ParseRawAudioCollectionsData(Dictionary<string, List<string>> rawAudioCollectionsData)
    {
        folderClips = rawAudioCollectionsData.ToDictionary(kvp => kvp.Key, 
            _ => new List<ProbabilityAudioClip>());
        audioCollections = new();
            
        foreach ((string folderName, List<string> tableNames) in rawAudioCollectionsData)
        {
            foreach (string tableName in tableNames)
            {
                string trimmedTableName = tableName.TrimStart('+');
                if (audioCollections.TryGetValue(trimmedTableName, out AudioCollection audioCollection))
                {
                    audioCollection.folders.Add(folderName);
                    audioCollection.includeVanillaClips |= tableName.StartsWith('+');
                }
                else
                    audioCollections[trimmedTableName] = new AudioCollection([folderName], tableName.StartsWith('+'));
            }
        }
    }

    private void Update()
    {
        while (logBufferQueue.Count > 0 && Time.unscaledTime - logBufferQueue.Peek().time > configLogSpamCooldown.Value)
        {
            recentLogs.Remove(logBufferQueue.Dequeue().log);
        }
    }

    private void TryLogAudio(string log)
    {
        if (recentLogs.Contains(log)) return;
        
        Logger.LogInfo(log);
        
        recentLogs.Add(log);
        logBufferQueue.Enqueue((log, Time.unscaledTime));
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
    class Start_Patch
    {
        [HarmonyPostfix]
        static void Start_Postfix()
        {
            Instance.StartCoroutine(Instance.RefreshAudioCollectionsRoutine());
        }
    }
    
    [HarmonyPatch(typeof(RandomAudioClipTable), nameof(RandomAudioClipTable.CanPlay))]
    class AudioLog_Patch
    {
        [HarmonyPostfix]
        static void PlayAudio_Postfix(RandomAudioClipTable __instance, bool __result)
        {
            if (!__result) return;
            Instance.TryLogAudio(__instance.name);
        }
    }
}