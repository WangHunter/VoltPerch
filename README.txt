VoltPerch 0.3.0
Peripheral battery monitoring for Windows
https://github.com/WangHunter/VoltPerch

REQUIREMENTS
Windows 10 version 2004 or later, or Windows 11, on an x64 PC.
Portable release builds include the .NET runtime. No separate runtime or SDK
installation is needed. Device and driver compatibility still applies.

INSTALL
Download the Windows x64 portable ZIP from the repository's Releases page
Extract the complete archive to a writable
folder such as D:\Tools\VoltPerch, then run VoltPerch.exe.
Keep the executable and its accompanying files together.

OPEN AND EXIT
Closing the main window sends the app to the tray by default.
Right-click the tray icon and choose Exit to quit completely.
Click the tray icon for a compact list of connected devices; choose Show all
to include offline and unconfirmed devices. Open main window shows details.
The tray uses a battery icon only; numeric percentages appear in device lists.
Hovering over the tray icon does not show a device tooltip.

LANGUAGE AND THEME
The default language is English. Open Settings > General > Language to choose
English or Simplified Chinese. Light, Dark and Follow system themes are also
available. Documentation is maintained in English.

BATTERY READINGS
A dash means no valid battery reading, not an empty battery.
System estimates carry an approximation mark. Coarse levels stay labeled
as levels. Disconnected devices keep their last valid reading marked as not live.
Receiver presence alone does not establish that a peripheral is connected.
VoltPerch keeps one latest reading per device, with no history or runtime forecast.

COMPATIBILITY
Model-specific queries are implemented for VGN V98pro V2 and SteelSeries
Rival 650. Rival 650 wireless receiver behavior still needs hardware verification.
Xbox controllers use Windows.Gaming.Input estimates or XInput battery levels.
Classic Bluetooth battery values depend on Windows exposing a usable property;
this channel has been exercised with JBL TUNE760NC. AirPods left, right and case
levels are displayed separately in the device and tray lists. Nearby Bluetooth LE
advertisements report approximately 10% resolution. Keep the case open or take the
earbuds out. The strongest recent
nearby signal is used because these broadcasts have no stable device identity.
Other generic BLE/GATT battery services and proprietary receiver protocols are
not implemented.
See README.md for the full compatibility table and verification limits.

REMOVE AND RESTORE
Right-click a device and choose Remove device. It is hidden from this app and
its saved battery/alert state is cleared; Windows pairing is unchanged.
Restore it under Settings > Devices & data > Removed devices.

UPGRADE
1. Exit the app completely from the tray menu.
2. Back up the data folder next to VoltPerch.exe.
3. Extract the new release into the same folder, replacing app files but
   preserving data.
4. Start VoltPerch.exe again.
Do not replace running application files. When moving the app, move data with it.
Re-enable launch at sign-in from the new location if needed.

LOCAL DATA
Settings, the latest battery readings, device identity mappings, removed-device
records and notification state are stored in data beside the executable.
The app does not upload device information, send telemetry or download updates.
Redact personal names and device identifiers before sharing diagnostic files.

SOURCE AND LICENSE
Source and contribution instructions: https://github.com/WangHunter/VoltPerch
MIT License. See LICENSE, THIRD_PARTY_NOTICES.txt and licenses for full terms.
