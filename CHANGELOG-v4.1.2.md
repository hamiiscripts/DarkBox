# DarkBox by Hamii — v4.1.2 changelog (12 Nov 2026)

## Setup mein progress-bar wala nizam (WebView2)

**Masla (v4.1.1):** auto-install khamoshi se fail ho gaya — user ko sirf
ye mila: "WebView2 could not be installed automatically." Na progress
nazr aaya, na asal wajah. Do khamosh wajahen theen:
1. Chhota bootstrapper use hota tha, jo asal runtime apne silent install
   ke andar chhup kar download karta hai — progress dikhana mumkin hi
   nahi tha, aur uska fail hona bhi nazar nahi aata tha.
2. Download/in install bina elevation aur bina TLS 1.2 ke chalta tha —
   .NET Framework ka default purani Windows par TLS 1.0 par gir sakta
   hai, jise Microsoft ke server mana kar dete hain; aur bina admin ke
   silent install per-machine register nahi hota.

**Nizam (v4.1.2):**

1. **Setup mein asal progress window** (`setup-boot/ProgressForm.cs`):
   "Downloading WebView2 Runtime from Microsoft…" ke saath live bar —
   MB + % (masalan "86 MB of 205 MB · 42%"), phir "Installing…" marquee,
   aur download ke doran Cancel button.
2. **Full standalone installer download hota hai**
   (`go.microsoft.com/fwlink/p/?LinkId=2124701`, ~205 MB — 12 Nov 2026
   ko check kiya: Content-Length 214,990,032). Isi liye asal progress
   dikhana mumkin hai. (Correction: runtime ~205 MB hai, pehle kaha gaya
   130 MB ghalat tha.)
3. **TLS 1.2 force** kiya jata hai download se pehle, dono jagah
   (setup-boot aur app).
4. **Elevated install**: installer pehle admin (UAC, aik dafa) ke saath
   chalta hai; UAC mana ho to per-user install ki koshish hoti hai.
   Kamyabi sirf tab mani jati hai jab runtime registry mein register
   ho jaye.
5. **Asal wajah ab chhupti nahi**: har step `%TEMP%\DarkBoxSetup.log`
   mein likha jata hai (download, exit code, elevation), aur fail hone
   par message mein asal wajah + log file ka rasta likha hota hai.
6. **App side bhi wahi nizam** (`src/WebView2Setup.cs` + `LockerForm`):
   standalone installer + TLS 1.2 + elevated install, aur status label
   par live MB/% dikhta hai.

**Ab bhi yaad rahe:** jis PC par WebView2 pehle se hai, wahan setup kuch
bhi download nahi karta — ye sab sirf us PC par hota hai jahan runtime
nahi hai, aur wo bhi aik hi dafa.

## Build (verified on this VM, .NET SDK 8.0.428 at ~/.dotnet)
```bat
dotnet build -c Release
copy bin\Release\net48\HamiiFolderLocker.exe pkg\HamiiFolderLocker\
mkdir build-payload && copy pkg\HamiiFolderLocker\* build-payload\
(cd build-payload && zip ../setup-boot/payload.zip *)
dotnet build setup-boot/DarkBoxSetup.csproj -c Release
```
