# DarkBox by Hamii — v4.1.7 changelog (12 Nov 2026)

## Asal wajah mil gayi: data folder lock hai, runtime nahi (locker.log)

**Log se sabit:** v4.1.6 app chali aur fixed-version runtime mil bhi
gaya — `16:13:04 Using fixed-version WebView2 runtime at
C:\Users\hamid\AppData\Local\Programs\HamiiFolderLocker\WebView2Runtime`
— lekin init phir bhi fail hua: `COMException (0x8007139F): The group
or resource is not in the correct state`. Uske baad har retry par:
`WebView2 data reset failed: Access to the path
'...\HamiiFolderLockerPro\WebView2' is denied.`

Matlab: runtime theek hai — WebView2 ka **data folder kisi purani
(stale) process ne lock karke rakha hai**, is liye na naya environment
ban raha hai, na folder side par ho raha hai. Ye error 14:11 se har
koshish par aa raha tha.

**Fix (v4.1.7):**

1. **Fresh data folder fallback** (`src/LockerForm.cs`,
   `InitWebWithFolder`): default folder move na ho sake (access
   denied) to app haar nahi manti — `WebView2-<timestamp>` naam ka
   bilkul naya data folder bana kar usi se khulti hai. Purana locked
   folder waise hi rehta hai, nuqsan koi nahi.
2. **Setup pehle purana DarkBox band karta hai**
   (`setup-boot/Program.cs`): wizard khulne se pehle chalte hue
   `HamiiFolderLocker.exe` processes band kar diye jate hain, taake
   tray mein chhupa purana instance folder lock na rakh sake.
3. **Tray text crash fix** (log 11:16): `BecomeGuard` mein tray icon
   ka text 63 chars se zyada hone par `ArgumentOutOfRangeException`
   aata tha (folder "NothingHereXD" wala case) — ab text kaat diya
   jata hai.

**User ko kya karna hai:** bas v4.1.7 setup chalana hai. Runtime
pehle se mojood hai, dobara kuch download nahi hoga — setup purana
instance band kare ga, wizard khule ga, aur app fresh data folder se
khul jaye gi.

## Build (verified on this VM, .NET SDK 8.0.428 at ~/.dotnet)
```bat
dotnet build -c Release
copy bin\Release\net48\HamiiFolderLocker.exe pkg\HamiiFolderLocker\
mkdir build-payload && copy pkg\HamiiFolderLocker\* build-payload\
(cd build-payload && zip ../setup-boot/payload.zip *)
dotnet build setup-boot/DarkBoxSetup.csproj -c Release
```
