LINEUP __VERSION__ FOR WINDOWS (__ARCHITECTURE__)
================================================

START LINEUP

1. Double-click Start-Lineup.cmd.
2. Keep the Lineup console window open while using the application.
3. If Windows Firewall asks, allow Lineup on private networks.
4. Open http://localhost:8080 if your browser does not open automatically.
5. Complete Settings > Device using your HDHomeRun's LAN IP address or hostname.

Always use Start-Lineup.cmd. Do not launch app\Lineup.Web.exe directly because
the launcher configures the safe user-local data locations.

STOP LINEUP

Select the Lineup console window and press Ctrl+C, or close that window.

PERSISTENT DATA

Settings and guide data:
  %LOCALAPPDATA%\Lineup\data

Generated XMLTV:
  %LOCALAPPDATA%\Lineup\xmltv\epg.xml

These folders are outside the extracted application folder and remain in place
when the application files are replaced during an update.

UPDATE LINEUP

1. Stop Lineup.
2. Download the newer ZIP for the same Windows architecture.
3. Extract it to a new folder.
4. Delete the old application folder after confirming the new version starts.

No .NET SDK or separate FFmpeg installation is required.

Documentation and support:
  https://github.com/am385/Lineup
