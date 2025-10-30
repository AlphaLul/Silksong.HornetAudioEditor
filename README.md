A Silksong mod that enables users to customize random audio with their own .wav audio clips. The mod comes prepackaged with some of Hornet's voice lines from the original Hollow Knight game (including shaw).
<br><br>

https://github.com/user-attachments/assets/6417d91a-ac40-4577-a875-26994b1873fd

# Guide
---
## Installation
1. Install BepInEx 5 for to your Silksong folder
2. Download `HornetAudioEditor.zip` and extract it to the `BepInEx/plugins` folder
3. Run the game
   - After running the game, you'll have access to the `audioCollections.json` file in the `HornetAudioEditor` folder

## Basic Usage
### Add audio clips
To add audio clips to the mod, place .wav files in the `Clips` folder or any of its subfolders.  
Your `HornetAudioEditor` folder should look something like this:
<pre>
HornetAudioEditor/
├─ Clips/
│  ├─ clip1.wav
│  ├─ clip2.wav
│  └─ subfolder/
│     └─ clip3.wav
│     └─ clip4.wav
├─ audioCollections.json
└─ HornetAudioEditor.dll
</pre>

### Configure `audioCollections.json`
To tell the game which folders to use for each audio table, edit the `audioCollections.json` file.
- **Key**: name of folder relative to `Clips`
  - Use an empty string `""` for the base `Clips` folder
- **Value**: array of names of the audio tables that should use the audio from the folder

Audio tables that are loaded on launch can be found in [`launchAudioTables.txt`](launchAudioTables.txt).  
Any other tables will require additional work to use, discussed at the end of the guide.

#### Example:
- `table1` loads audio from the base `Clips` folder
- `table3` and `table4` load audio from the `Clips/subfolder` folder
- `table2` loads audio from both folders
```json
{
  "": [
    "table1",
    "table2"
  ],
  "subfolder": [
    "table2",
    "table3",
    "table4"
  ]
}
```
## Keeping Vanilla Audio
If you want an audio table to keep its original (vanilla) audio alongside the modded audio, add a `+` in front of the table name.

#### Example:  
- `table1` fully replaces its audio with only the modded audio
- `table2` adds the modded audio while keeping its vanilla audio
```json
{
  "subfolder": [
    "table1",
    "+table2"
  ]
}
```
## BepInEx Config
A BepInEx config file can be found at `BepInEx/config/alphalul.HornetAudioEditor.cfg`. This file lets you customize some basic settings.

| Setting | Default | Description  |
| ------------ | ------------- | ------------- |
| Enabled | true | Whether the mod is active. Set to false to disable the mod. |
| LogAudio | false | Whether to log the name of a `RandomAudioClipTable` when it plays a sound. Useful for finding table names. |
| RefreshOnSaveQuit | true | Whether to refresh the mod after returning to the title screen. |
| RefreshHotkey | None | Optional hotkey to refresh the mod at any time. Use to load tables unavailable at launch. |

### ⚠️ Finding audio table names
To use `LogAudio` properly, you need to enable the BepInEx console to read the logs in real time. To do so:
1. Open `BepInEx/config/BepInEx.cfg`
2. Find the `[Logging.Console]` section
3. Set `Enabled = true`

This makes a console window launch along with the game that displays logs. Now you'll see the name of any `RandomAudioClipTable` in the console whenever it plays a sound, allowing you to tell which name goes with which sounds.

---
### ⚠️Important notes
Mono .wav files seem to play quieter than stereo clips. If your clips are sounding quieter than expected, consider converting them to stereo.
 
This mod was primarily made to edit Hornet's audio, but it works with other audio as well. Any audio that uses Team Cherry's `RandomAudioClipTable` internally works, so long as the table is loaded on launch (listed in [`launchAudioTables.txt`](launchAudioTables.txt)).

If it isn't, you'll have to set a refresh hotkey. These tables unload and reload pretty frequently, so you'll have to use the refresh hotkey periodically. The mod is best used with tables that are loaded on launch.
