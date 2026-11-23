# PC OnSong Bridge

PC OnSong Bridge sends a tablet’s spoken song and section cues to laptop speakers, a USB mixer, or another Windows playback device. The tablet stays in charge of OnSong. The bridge can also use a Windows microphone to change songs or scroll a chart.

**[Download the Windows beta](https://github.com/tommykmusic/pc-onsong-bridge/releases/tag/v0.8.1)**. Choose `PC-OnSong-Bridge-Setup.exe` to install under your Windows account. The release ZIP also includes the installer and source.

The beta installer is unsigned. If your browser says it’s uncommon, check that you’re on the project’s GitHub release page. In the download folder, run `Get-FileHash .\PC-OnSong-Bridge-Setup.exe` in PowerShell and compare the full SHA-256 with the installer entry in `SHA256SUMS.txt` from that release. For a matching download you trust, the Windows SmartScreen reputation prompt offers **More info → Run anyway**. Stop if the checksum differs or your security software reports malware.

| What has been tested | What to expect in this beta |
| --- | --- |
| Cue audio over Wi-Fi | A TCL Android tablet running OnSong Premium sent song and section cues over NDI® to the selected Windows speakers. A physical iPad and the X32-to-IEM route still need a soundcheck. [NDI](https://ndi.video/) |
| Cue audio and voice control over Android USB | A TCL tablet played cues through Windows. A musician using the laptop mic changed songs and scrolled the chart. Other OnSong actions can be assigned to shortcuts. |
| Voice control over native OnSong Connect Wi-Fi | The tablet paired and allowed chart reads. OnSong 2026.5 for Android rejected chart writes during the physical trial. Next, previous and scroll have passed against a simulated tablet. |

## Requirements

- A 64-bit Windows 10 (version 1607 or later) or Windows 11 PC with .NET Framework 4.8 or later. The in-app scrcpy download is for x64 Windows.
- For Wi-Fi cue audio, use a tablet with OnSong Premium, Vocal Cues, and a selectable NDI switch under **External Video → Live**. The tablet and PC must be on the same local network with device-to-device traffic allowed. The PC can use Ethernet.
- For Android mirroring via USB or paired wireless debugging, Android 12 or later is recommended. On Android 11, unlock the tablet before starting the bridge. The following scrcpy requirement applies to Android mirroring.
- scrcpy and its companion `adb.exe`. **Download scrcpy** fetches the official x64 v5.0.1 package, verifies the published SHA-256, and extracts it under `%LOCALAPPDATA%\PC OnSong Bridge\tools`. For offline setup, extract the complete official ZIP ahead of time. [Official Windows download and checksum](https://github.com/Genymobile/scrcpy/blob/v5.0.1/doc/windows.md)
- A Windows playback device for cue audio. Voice commands use a Windows recording input such as a laptop microphone and a **System.Speech-compatible English desktop recognizer**. The app prefers US English and uses another installed English recognizer when available. See [Windows language and speech settings](https://support.microsoft.com/en-us/windows/hardware/input-devices/manage-the-language-and-keyboard-input-layout-settings-in-windows).

## Install and connect over Wi-Fi

1. Run `PC-OnSong-Bridge-Setup.exe`. Open **PC OnSong Bridge** from the Windows Start menu and click **Find my tablet**. For first-time setup, **Set up Wi-Fi playback** opens the official NDI 6 Runtime installer. Complete its wizard. The bridge checks the downloaded installer against the SHA-256 for the vendor-signed version tested with this release. You can also install the runtime directly from the vendor. [NDI runtime](https://ndi.link/NDIRedistV6) · [NDI](https://ndi.video/)
2. On the tablet, enable **Song** and **Section** in **Utilities → Settings → Live Settings → Vocal Cues**. In **External Video → Live**, turn on **Send Audio** and **NDI**, then tap **Go Live**. The tablet shows the name to select on the PC. Start with 720p on Wi-Fi. RTMP can stay off.
3. Click **Find my tablet**. On the first search, Windows may ask for network access for **PC OnSong Bridge**. Choose the network profile you use for your band. Select the stream name shown by OnSong and the desired **Windows output**, then click **Start Wi-Fi cues**. Change songs and tap a section. The window shows the received signal and the selected output. Keep this window open to receive cues. You can use the main window for Android voice commands while Wi-Fi audio plays.
4. If the list is empty, start another search and tap **Stop Streaming**, then **Go Live** on the tablet. For a network that filters discovery announcements, use **Enter tablet address** with the tablet’s Wi-Fi IPv4 address. The bridge looks up the correct streaming port. After a verified connection it remembers the tablet and output for the next soundcheck.
5. When finished, click **Stop Wi-Fi cues** on Windows and **Stop Streaming** on the tablet. Other receivers on the local network can see the selected projection and hear the main mix while OnSong is live. [OnSong Live Streaming](https://onsongapp.com/docs/interface/menubar/external-video-menu/livestream/)

### Optional components in the installer

On a first install, **Choose optional components after installation** is selected. Choose **Wi-Fi cue audio** to open the NDI runtime wizard, **Android USB cues and controls** to install the verified scrcpy package with ADB, or **Voice commands** to open Windows language settings if an English desktop recognizer is missing. Choose **Set up selected**. Components already available appear as **Ready**. You can use **Open bridge** or **Finish** to proceed without downloading tools, or run the installer again to add a component later.

### Wireless voice control (experimental — developer key required)

> **Status:** For working voice commands today, use **Android USB or wireless debugging** below. Native OnSong Connect Wi-Fi voice control requires an OnSong-issued developer key, and OnSong 2026.5 for Android currently returns HTTP 401 on chart changes.

**Wi-Fi voice control** uses OnSong Connect in OnSong 2026 or later. It works with USB unplugged and tablet streaming off. On the PC, open **Find my tablet → Wi-Fi voice control**. The bridge fills in the selected stream’s address or the last saved tablet address. You can also enter the tablet’s Wi-Fi address yourself.

Only this optional Wi-Fi voice feature needs an OnSong-issued developer application key. In **Wi-Fi voice control**, choose **Developer key…** to enter a key issued for your application. Windows protects the saved key and tablet pairing token for the current Windows user. Developers can also provide the key through the `ONSONG_CONNECT_API_KEY` environment variable and reopen the bridge. The public release contains no application key. Use Connect on a trusted private network because OnSong 2026 sends credentials over local HTTP. OnSong may need internet access on the tablet to verify the application key. [OnSong developer applications](https://onsongapp.com/developers/licensing/) · [Connect authentication](https://onsongapp.com/developers/connect/api/auth.php)

Choose **Check tablet**, confirm the tablet name, then choose **Pair with tablet**. If OnSong shows an approval prompt, approve on the tablet and choose **Pair with tablet** again on the PC. Open a set with at least two songs. Choose your laptop microphone, click **Listen for command**, wait for **Listening**, and say **“next song.”** Read the result before clicking **Listen for command** again to say **“previous song.”** When OnSong confirms the change, the time shown runs from the detected end of your phrase until the PC receives the updated chart state. The tablet’s screen animation can have different timing.

**TCL soundcheck, OnSong 2026.5 for Android.** With USB unplugged, the bridge paired over Wi-Fi, read the active chart and set order, and recognized “next song” through the laptop microphone. The chart stayed on the starting song. The tablet returned HTTP 401 Unauthorized to a diagnostic `POST /state` with no changed fields. The same credentials continued to read the chart. This prevented measuring a successful song change in either direction. The next, previous and scroll commands have passed against a simulated server. [OnSong chart control](https://onsongapp.com/developers/connect/api/state.php)

## Connect with USB or Android mirroring

1. Run `PC-OnSong-Bridge-Setup.exe` and open **PC OnSong Bridge** from the Start menu. The unsigned installer installs for the current Windows user. You can remove it under **Settings → Apps**. Your preferences and any downloaded scrcpy tools stay in `%LOCALAPPDATA%\PC OnSong Bridge`.
2. If the scrcpy field is empty, click **Download scrcpy**. If you already have it, use **Locate scrcpy** and select `scrcpy.exe` in its extracted folder. Once configured, these controls are under **scrcpy setup**. The window title and **About** button show the version you are running.
3. For the first soundcheck, connect the tablet directly to the PC with a USB data cable. On Android, turn on **Developer options → USB debugging** and approve the PC on the tablet. Click **Refresh tablets** and select your tablet.
4. Open OnSong and enable **Vocal Cues** under **Utilities → Settings → Live Settings**. Enable **Song** and **Section** so you have two cues to try during soundcheck.
5. Under **Expected output**, choose your laptop speakers or the playback device for your mixer. Click **Play quiet test tone** to check that device at low level. For the X32, set up the cue return as described below.
6. Click **Start cue audio** with the default 75 ms buffer. The app checks which Windows playback device has an active session for this copy of scrcpy. If it shows **Wrong cue output**, click **Open Windows Volume Mixer** and assign scrcpy to your intended device. On Windows 10, use **App volume and device preferences**. Stop and restart cue audio if Windows keeps the old assignment.
7. Once **Output matched** appears, tap a song cue and a section cue in OnSong. Listen on the laptop or in each intended IEM mix. When using a mixer, also verify silence at the house output.

Audio is forwarded from the tablet’s playback, including other audio Android sends over the same output. The tablet’s local playback is redirected while the bridge is running. Use Do Not Disturb during a show and turn off any OnSong backing tracks, click, or other audio you want to keep out of the cue return. Increase the buffer gradually if you hear dropouts.

## Route the X32 to IEMs

1. Connect the Windows PC to the USB-B port on the X32 Rack’s rear expansion card. Get the driver that matches the card, [X-USB](https://behringer.com/en/products/0604-AAA) or [X-LIVE](https://behringer.com/en/products/0606-ABU).
2. In X32 Edit, confirm which **Card** channels carry the chosen Windows playback pair. Start with **Card 1/2**. If Aux 1 and 2 are available, use **Routing → Inputs → Aux In Remap → Card 1–2**. On firmware 4.x, you can instead use **User In** to patch the selected Card channels into unused inputs. Preserve the existing source assignments in any input block you repatch.
3. Name the resulting mixer channels **OnSong Cues**, turn their **Main LR** and **M/C** assignments off, and bring up their sends to the required IEM mix buses at low level. Confirm the cues reach each intended IEM mix and that the house output stays silent.
4. For live talkback, send the X32 talkback to the desired monitor mix buses on the mixer. For optional voice commands, send a clean talkback or command mic signal in the other direction over USB. Use a spare **Out 1–16** or **P16** routing slot with **Talkback** or a command mic’s direct out as the source, then route that slot through **User Out** to the recording pair via **Routing → Card**. For a stereo Windows endpoint, keep its other channel silent or route the same isolated mic there. Keep the PC cue return and the monitor mix out of the capture pair to avoid feedback and false commands. Check which Card output pair your Windows recording endpoint captures before changing a patch you already use for a recording.

Mixer labels vary with firmware. See the [X32 Rack manual](https://cdn.mediavalet.com/aunsw/musictribe/IzBxMjc_gEeQhNLIdo58oA/kZ5qrv38DEa7dCuKYdoqeg/Original/8849854726174.pdf) for Aux input, card, and talkback routing. Save your current X32 scene before repatching for the first test.

## Voice commands and latency

1. Open a middle song in a practice set on the tablet. On the PC, select your recording input under **Voice navigation → Microphone**. For low-latency song changes, choose **Fast song shortcuts (foreground)** and click **Check navigation**. On the TCL used for the soundcheck, **Ctrl+Shift+N** is assigned to **Next Song** and **Ctrl+Shift+P** to **Previous Song**.
2. On another tablet, open **Command library and tests** on the PC. In OnSong, open **Foot Pedals → Shortcuts**. Select **Next song** in the bridge, click **Teach shortcut to tablet**, and choose **Navigation → Song → Next** on the tablet. Repeat with **Previous song → Navigation → Song → Previous**. Return to the chart and test the two manual song buttons. The bridge keeps the verified foreground check on each keypress.
3. To measure a command, select its phrase in **Command library and tests**, click **Speak and send**, wait for **Listening**, and say the phrase. In **Test history**, speech timings start at the first or last part of the phrase identified by the recognizer. **Android acknowledgment** means Android accepted the command. For song changes, **Observed chart change** includes the time the bridge takes to read the new set position. Use **It worked** or **No effect** to record what you saw. Repeat the phrase to see a median and range for the session.
4. For live use, click **Listen for command** or enable the global **F8** hotkey and press **F8**. When **Listening** appears, say one of the ready phrases. Windows processes short blocks of microphone audio while you speak and the bridge sends the first completed, accepted command. The listening window limits how long it waits for a phrase. Fast live navigation sends the shortcut immediately after recognition. The diagnostic **Speak and send** test reads the starting chart before opening the mic and confirms the new chart after sending. Cue audio can stay off during these tests.

### More OnSong actions

Search the library by action or category. To set up “next section,” search **next section**, select the **Navigation** result, then use **Add phrase**. Enter the spoken words and an unused shortcut, then click **Save mapping**. On the tablet, open **Actions Editor → Shortcuts**. Click **Teach shortcut to tablet** on the PC, choose **Navigation → Section → Next** on the tablet, and close the editor. You can also register a shortcut by holding the same keys on a connected keyboard. With the chart open, use **Send once**, then **Speak and send**. Mapped shortcuts act on the current OnSong screen. Use the chart or connected equipment to confirm their effect. [OnSong Shortcuts](https://onsongapp.com/docs/interface/menubar/utilities-menu/editors/actions/shortcuts/)

For a named target, map a phrase such as “go to Amazing Grace” to **Song → Switch** with that song selected in OnSong, or “chorus” to **Section → Switch** with that section selected. Each named target needs its own OnSong assignment. **New custom action** also accepts actions provided by your edition or equipment. Actions involving audio tracks, projection, MIDI, or lighting need those features configured on the tablet. The library includes reference entries for Apple keyboard defaults and labels them accordingly. [OnSong actions menu](https://onsongapp.com/docs/interface/menubar/utilities-menu/editors/actions/menu/) · [OnSong voice control](https://onsongapp.com/docs/interface/menubar/utilities-menu/editors/actions/voice/)

| Tablet control | How song changes are sent |
| --- | --- |
| Fast song shortcuts (foreground) | Sends the dedicated Next or Previous shortcut to OnSong. Use after assigning and verifying both shortcuts on the selected tablet. |
| OnSong song swipes (foreground) | A left swipe advances and a right swipe goes back. Enable **Swipe Through Sets** in OnSong and keep the chart visible. |
| Automatic (OnSong links or song swipes) | Checks both song links. If either is unavailable, the bridge checks the song viewer and uses swipes. |
| OnSong arrow shortcuts (foreground) | Assign **Right Arrow** to **Next Song** and **Left Arrow** to **Previous Song** in OnSong. Check each with the manual song buttons. |

**Test microphone** works without a tablet. It shows the selected input’s peak level and checks the phrases “next song” and “previous song.” When the X32 or another interface is connected, select its Windows recording input and repeat the soundcheck. Live talkback can continue on the mixer.

## Optional network connection and tablet power

### Wi-Fi soundcheck results

On a TCL Android tablet running OnSong 2026.5, the bridge found the projection by name and received 48 kHz stereo cues. One integrated trial moved to the next song, selected a section and returned to the starting song. Windows independently measured three audio bursts from the bridge process on the chosen Realtek speaker endpoint. From the start of each tablet action to the first Windows meter reading above −60 dBFS, the respective times were approximately 0.62 s, 0.50 s and 0.22 s. USB automated the taps and keyboard shortcuts for repeatability. Wi-Fi carried the audio. Speaker acoustics, microphone recognition, an iPad and the X32 were outside that measurement.

In a second trial, a test harness loaded the installed v0.7.0 receiver, found the same named stream at the tablet’s known address in 76 ms, and played another three cues through Realtek. Windows detected the next-song cue in 0.67 s, the section cue in 0.16 s and the previous-song cue in 0.22 s. The receiver reported 372,000 stereo sample frames played and zero output frames dropped during this run.

After the audio-driver fix below, the installed v0.7.0 app received another section cue from the TCL. The first Windows meter reading on the selected speakers was 0.58 s after the start of the tap, with a peak of −5.21 dBFS. Reopening **Find my tablet** selected the same live tablet and speakers automatically.

An earlier test with two independent Windows receivers timed arrival before Windows playback. The first audible samples arrived in 0.17–0.20 s for the next-song cue, 0.12–0.13 s for a tapped section and 0.16–0.17 s for the previous-song cue across two trials per action.

The release receiver also passed a sample-by-sample test with a local NDI sender and the Realtek output. After 65 seconds without audio, a test held the first output write for 425 ms to simulate the driver waking up. All 44,901 stereo frames across four cues reached driver completion, including after restarting the sender and restarting the receiver. The bridge receives incoming audio while Windows is busy and submits it as soon as the driver accepts it. Hover over the soundcheck meter to see received packets, NDI receiver drops, played sample frames and output drops.

### Android wireless debugging for built-in audio and commands

1. Join the same private band network on the tablet and PC. Enable **Developer options → Wireless debugging** on Android and approve the network on the tablet if prompted.
2. **First time with this PC:** open **Pair device with pairing code** on Android. In the bridge, open **Wireless setup → Find tablets**. Choose the **Pairing IP:port** that matches the tablet, enter its six-digit code and click **Pair**. If discovery is unavailable, type the pairing address displayed by Android.
3. **At the next gig:** with Wireless debugging enabled, click **Refresh tablets**. Android can automatically reconnect a paired tablet on the same network. Select it and click **Start cue audio**. If it is missing, click **Wireless setup → Find tablets**. Choose the tablet’s **Connection IP:port** and click **Connect**, or enter its current address from the main Wireless debugging screen. The pairing and connection ports differ and can change. [Android wireless debugging](https://developer.android.com/tools/adb#wireless)
4. You can prepare Wi-Fi while USB cues are running. At a break, click **Stop**, then **Refresh tablets**, select the wireless entry and click **Start cue audio**. Keep the USB cable connected until a cue reaches the IEMs over Wi-Fi.

A direct USB connection carries audio data and may charge the tablet. Verify that the battery holds charge at your performance brightness. If using network audio, you can charge the tablet directly or use a USB-C hub that supports USB Power Delivery and Ethernet on your tablet. Wired Ethernet carries network traffic only if the tablet, hub, and Android debugging connection support that combination. Confirm empirically before relying on it at a show.

## Soundcheck checklist

- Trigger one song announcement and one section announcement. Verify both in the intended IEMs and listen at the house output for silence.
- Check current and next/previous songs on the master tablet and at least one OnSong Connect follower.
- If using voice commands, check each phrase from the isolated mic and verify that regular talkback still reaches the intended IEMs.
- Disconnect and reconnect the cable or network once. Refresh and restart audio if needed. Verify a cue again.
- Check that nearby Wi-Fi activity, full performance screen brightness, and regular set navigation are compatible with your tested buffer and power setup.

In v0.6.0, a musician used the laptop’s built-in mic and **Fast song shortcuts** with a USB-connected TCL running OnSong 2026.5. From the estimated end of the spoken phrase, the laptop observed the following times in one trial per direction. The screen timings come from sampled tablet frames. After “previous song,” the original chart, set position, and scroll position were verified.

| Live phrase | Android acknowledged | Chart began moving | New chart nearly settled |
| --- | ---: | ---: | ---: |
| Next song | 0.39 s | 0.52 s | 0.74 s |
| Previous song | 0.33 s | 0.40 s | 0.68 s |

A TCL tablet running OnSong 2026.5 played vocal cues over USB through a Windows laptop. On that rig, OnSong did not advertise song navigation links, and the right-arrow command left the chart in place. With **OnSong song swipes (foreground)** selected in v0.4.1, the laptop’s built-in microphone recognized “next song” and “previous song.” The first command advanced one chart, and the second returned to the original chart. Both movements were confirmed on the tablet.

To repeat the historical swipe check, choose **OnSong song swipes (foreground)** and open **Command library and tests**. For fast song changes, use the dedicated shortcuts described above. Wireless playback and timing still need a soundcheck on the show network.

In the v0.5.0 USB soundcheck on the same TCL, the Windows recognizer and bridge advanced and returned one song, then scrolled down and up on the same chart. Both the starting chart and the positions of its visible contents were verified afterward. The tablet’s Shortcuts editor also accepted a new **Next Section** assignment, and sending that shortcut moved the chart within the same song. Each additional mapping can be tested individually in the library.

One v0.5.0 trial in each direction used the laptop microphone and timed from the end of the musician’s phrase. For **Next song**, Android acknowledged the command at 5.09 s, screen samples showed the new chart at approximately 5.4–6.8 s, and the bridge verified its identity by 8.67 s. For **Previous song**, those measurements were 4.72 s, approximately 5.0–6.4 s, and 8.25 s. The ranges reflect USB screen sampling during this test. Run repeated trials on your own setup to measure typical performance.

In a separate test, reading the tablet’s chart layout took 3.40 s. A generated “next section” phrase sent the saved keyboard shortcut, advanced within the song, and received Android acknowledgment 0.31 s after dispatch began. That interval excludes speech capture and recognition. The original chart and scroll position were restored.

## Quick troubleshooting

- **No Wi-Fi stream found:** Confirm both devices are on the same band Wi-Fi (not a guest network with client isolation), start another search, and tap **Stop Streaming** then **Go Live** in OnSong. If discovery is blocked, click **Enter tablet address** and type the tablet’s Wi-Fi IPv4 address.
- **Microphone level or recognition problems:** Check the peak after **Listen for command** or **Test microphone**. Below −60 dBFS, check the selected input, mute switch and mic connection. Between −60 and −40 dBFS, try speaking closer or raising input gain. At −1 dBFS or higher, lower the gain to leave some headroom. If you connect a USB mic or mixer after opening **Wi-Fi voice control**, click **Refresh** next to the microphone picker.
- **USB tablet not listed:** Use a USB data cable (not charge-only), turn on **Developer options → USB debugging**, unlock the tablet to accept the **Allow USB debugging** prompt, then click **Refresh tablets**.

## Build from source

Run this command from the project root in Windows PowerShell.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -OutputDirectory .\dist
```

The build produces `dist\PC-OnSong-Bridge.exe` and `dist\PC-OnSong-Bridge-Setup.exe`. Add `-Package` to also create a versioned ZIP with the installer, portable app, source, tests, routing guide, and MIT license. Close the running bridge before updating or uninstalling.

To run the Windows regression tests, use the following command. The tests simulate Android device discovery, connection loss, reconnect, and navigation. They check live output detection with digital silence and exercise speech recognition with synthesized audio. The package test downloads the pinned scrcpy ZIP into a temporary directory. To reuse an existing copy, pass `-ScrcpyArchive` and its path.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\test.ps1
```

For the full catalog audit, run `powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\voice\run.ps1 -CatalogAudit`. It synthesizes each suggested phrase, checks the Windows recognizer against the combined grammar and sends each configured shortcut to a simulated tablet. Test actual action effects on your own OnSong setup.

To measure recognition latency, run `powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\voice\run.ps1 -Latency`. It feeds speech at recording speed and times the interval from the last audible sample to the command returned by Windows. It also exercises background noise and phrases that share their opening words. Tablet response is measured separately during a soundcheck.

## Community feedback and contributions

Soundcheck reports from iPad users, bands with an X32 or other USB mixer, and musicians testing OnSong Connect chart control are welcome. Open an issue with your bridge version (shown in **About**), Windows and OnSong versions, tablet and mixer models, USB or Wi-Fi, and the steps and result you observed. For a cue-routing report, say where you heard the song and section cues. Remove private setlists, lyrics, stream names, IP addresses, device serial numbers, pairing codes and application keys from screenshots or logs before posting. [Report a soundcheck or bug](https://github.com/tommykmusic/pc-onsong-bridge/issues)

To contribute a change, build from source, run the relevant tests above and describe the setup you tested. Please use fictional charts or device identifiers in any fixtures. [Open a pull request](https://github.com/tommykmusic/pc-onsong-bridge/pulls)

## License and third-party software

The bridge source, documentation and included Windows icon are available under the [MIT license](LICENSE). This is an independent community project.

- OnSong is a separate tablet app from OnSong LLC. Native Wi-Fi cue audio requires the OnSong features listed above. [OnSong](https://onsongapp.com/)
- Android runs on the tablet. Android mirroring and commands use scrcpy and Android Debug Bridge (`adb`). The optional Windows download comes from the scrcpy project and carries its own license and component notices. [scrcpy license](https://github.com/Genymobile/scrcpy/blob/v5.0.1/LICENSE) · [Android Debug Bridge](https://developer.android.com/tools/adb)
- Native Wi-Fi cue audio uses the separately installed Windows NDI 6 Runtime, distributed by its vendor. NDI® is a registered trademark of Vizrt NDI AB. [NDI](https://ndi.video/)

## Reference links

- [OnSong for Android](https://onsongapp.com/docs/platforms/android/), [Vocal Cues](https://onsongapp.com/docs/interface/menubar/utilities-menu/settings/live-settings/vocal-cues-settings/), [Actions Editor](https://onsongapp.com/docs/interface/menubar/utilities-menu/editors/actions/), and [song navigation URLs](https://onsongapp.com/developers/open-song/).
- [scrcpy audio forwarding](https://github.com/Genymobile/scrcpy/blob/master/doc/audio.md) and [Android USB and wireless debugging](https://developer.android.com/tools/adb).
- [Windows 11 app audio output routing](https://support.microsoft.com/en-au/windows/hardware/audio/fix-app-audio-not-working-while-system-sounds-work-in-windows) and [Windows 10 app audio output routing](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/default-audio-endpoint-selection).
