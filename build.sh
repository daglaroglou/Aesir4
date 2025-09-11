# Description: Build Aesir project
# Author: daglaroglou
# Credits: Adrilaw (Odin4), Benjamin Dobell (Heimdall), TheAirBlow (Thor)
dotnet publish -p:PublishProfile=linux-amd64
dotnet publish -p:PublishProfile=linux-arm64