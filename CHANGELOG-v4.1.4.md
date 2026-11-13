# DarkBox by Hamii — v4.1.4 changelog (12 Nov 2026)

## Fixed-version runtime: system install ki zaroorat hi khatam

**Masla:** is PC par Evergreen installer do dafa exit code 0 ke saath
chala, lekin runtime register nahi hua — is liye system-wide install
ka rasta yahan band hai. User ne khud fixed-version runtime package
download kar liya (`Microsoft.WebView2.FixedVersionRuntime.157.0.4341.46.x64`).

**Nizam (v4.1.4):**

1. **Setup khud file dhoondhta hai** (`setup-boot/Program.cs`):
   Downloads, Desktop ya setup wali folder mein
   `Microsoft.WebView2.FixedVersionRuntime.*` package mile to use
   Windows ke apne `expand.exe` se `%LOCALAPPDATA%\Programs\HamiiFolderLocker\WebView2Runtime`
   mein extract kar deta hai — phir Evergreen download/install ka
   sawal hi nahi uthta.
2. **App khud use karti hai** (`src/WebView2Setup.cs` + `LockerForm`):
   `GetFixedVersionFolder()` pehle app wali folder mein, phir app-data
   mein `WebView2Runtime` dhoondhta hai; mile to `CreateAsync` ko
   browserExecutableFolder de diya jata hai. Fixed-version runtime ko
   na registry chahiye, na system install — sirf files.
3. `IsRuntimeInstalled` ab fixed-version folder ko bhi "installed"
   manta hai, to dobara download ka prompt nahi aata.

**User ko kya karna hai:** kuch nahi — file Downloads mein rehne dein
aur v4.1.4 setup chalayen. Agar setup file na dhoondhe to use setup
wali folder mein rakh kar dobara chalayen.

## Build (verified on this VM, .NET SDK 8.0.428 at ~/.dotnet)
```bat
dotnet build -c Release
copy bin\Release\net48\HamiiFolderLocker.exe pkg\HamiiFolderLocker\
mkdir build-payload && copy pkg\HamiiFolderLocker\* build-payload\
(cd build-payload && zip ../setup-boot/payload.zip *)
dotnet build setup-boot/DarkBoxSetup.csproj -c Release
```
