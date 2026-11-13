# DarkBox by Hamii — v4.1.1 changelog (12 Nov 2026)

## WebView2 — user ko ab kuch haath se nahi karna

**Masla:** DarkBox ki window Microsoft Edge WebView2 Runtime par chalti hai.
Jis PC par wo runtime nahi tha, user ko "WebView2 Runtime is needed"
ka box milta tha aur use khud Microsoft se ~130 MB download karke install
karna parta tha — har naye user ke liye aik extra kaam.

**Nizam (v4.1.1):**

1. **Setup khud check karta hai, installer khulne se pehle**
   (`setup-boot/Program.cs` — `EnsureWebView2()`): plain-WinForms
   bootstrapper registry mein runtime dhoondhta hai (per-machine aur
   per-user, 64-bit aur 32-bit view). Mil jaye to setup aage barhta hai —
   **kuch download nahi hota**. Na mile to pooch kar Microsoft ka official
   Evergreen bootstrapper (`go.microsoft.com/fwlink/p/?LinkId=2124703`)
   download karke `/silent /install` se install karta hai, phir installer
   kholta hai. Ye step is liye bootstrapper mein hai ke installer wizard
   khud bhi WebView2 window hai — runtime ke baghair wo khul hi nahi sakta.
2. **App bhi khud retry karti hai** (`src/WebView2Setup.cs` +
   `LockerForm.InitWeb`): agar runtime installed hai lekin phir bhi window
   na khule to corrupt WebView2 data folder ko `WebView2.corrupt-<time>`
   karke aik dafa dobara try karti hai. Agar runtime nahi hai to "Install
   it now automatically?" pooch kar background mein install karke window
   khud khol leti hai — status dikhata hai ke install ho raha hai.
   Sirf tab purana manual download-page prompt aata hai jab auto-install
   fail ho jaye ya user mana kar de.
3. **Offline ho to setup rukta nahi** — download fail ho to warning dekar
   aage barhta hai, aur app phir apni retry dikhata hai.

**Size ki sachai:** runtime ~130 MB hai, 300 MB nahi — aur wo aik dafa ka
hai, Microsoft ka hai, aur har app (Edge, Office, Teams jaisi) ke liye
shared hai. Zyada tar Windows 10/11 PCs par wo pehle se hota hai, to
un par setup kuch bhi extra download nahi karta.

**Jo ab bhi nahi badla:** bilkul zero-download sirf tab mumkin hai jab
poori UI WebView2 se hatakar native WinForms mein dobara likhi jaye —
wo aik bara rebuild hai (poora panel dobara banana), is liye v4.1.1 mein
auto-install hi sahi nizam hai.

## Build (verified on this VM, .NET SDK 8.0.428 at ~/.dotnet)
```bat
dotnet build -c Release
copy bin\Release\net48\HamiiFolderLocker.exe pkg\HamiiFolderLocker\
mkdir build-payload && copy pkg\HamiiFolderLocker\* build-payload\
(cd build-payload && zip ../setup-boot/payload.zip *)
dotnet build setup-boot/DarkBoxSetup.csproj -c Release
```
