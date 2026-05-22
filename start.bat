@echo off
cd /d "%~dp0"
start "Tunnel" cmd /c tunnel.bat
start "Webhook" cmd /c node webhook.js
exit
