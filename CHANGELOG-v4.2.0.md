# DarkBox by Hamii — v4.2.0 changelog (12 Nov 2026)

## 1. Har folder ki apni lock mode: Just Lock ya High End Lock

**Masla:** har folder lock hote waqt AES-256 encrypt hota tha — low-end
PC par bara folder lock/unlock karne mein bohat time lagta tha.

**Nizam (v4.2.0):** har folder ke paas ab apni lock mode hai, folder
row par lock icon se badli jati hai:

- **Just Lock (default)** — folder vault mein move ho kar hidden ho
  jata hai, DarkBox ke baghair khul nahi sakta. Koi encryption nahi,
  is liye lock/unlock foran hota hai, chahe folder kitna bhi bara ho.
  Phir bhi poora lock hai — sirf chhupana nahi: folder apni jagah se
  hat kar vault mein hota hai aur uski jagah sirf DarkBox ka shortcut
  rehta hai.
- **High End Lock** — vault + AES-256 encryption. Files vault milne
  par bhi parhi nahi ja sakti. Bara folder ho to waqt lagta hai — yehi
  purana nizam hai.

**Tafseel:**
- Naye folder **Just Lock** se lock hote hain (default). Settings mein
  "New folders use High End Lock by default" on karne se naye folder
  High End se lock honge.
- Purane folder: jo pehle se encrypted hain (ya pending) wo **High End**
  rehte hain — un ka nizam nahi badla. Baqi Just Lock ban jate hain.
- Row par tag dikhta hai: `JUST LOCK` ya `HIGH END` (+ `AES ENCRYPTED`
  jab files encrypted hon).
- Mode badalne par: locked folder ko High karne se us ki files foran
  encrypt ho jati hain (vault key memory mein ho to); Just karne se
  pending encryption cancel ho jati hai — pehle se encrypted files
  agli unlock par decrypt hoti hain, jaise pehle hota tha.
- Lock hone ki popup ab mode bhi batati hai ("Just Lock — no
  encryption, opens instantly").

## 2. Locker se unlock ab slow nahi

**Wajah (code se sabit):** locker (`--unlock` window) mein pattern
dete hi `UnlockDek` pending-encryption ka kaam bhi queue mein daal
deta tha — wo har pending folder ko encrypt karta hai — aur asal
unlock uske PEECHE line mein khara rehta tha, kyun ke kaam aik hi
worker queue se hota hai. Admin panel mein ye kaam sign-in par pehle
hi ho chuka hota hai, is liye wahan unlock fast lagta tha.

**Fix (v4.2.0):**
- Locker unlock ab pending-encryption pass chalaye baghair sirf vault
  key kholta hai — pending kaam agli admin sign-in par hota hai.
- User ki unlock ab queue mein aage lagti hai (`EnqueueJobFront`) —
  peeche khare maintenance kaam (pending encryption / healing) uska
  intezar nahi karwate. (Jo kaam pehle se chal raha ho, wo pehle
  khatam hota hai — queue sirf line badalti hai.)

## Build (verified on this VM, .NET SDK 8.0.428 at ~/.dotnet)
```bat
dotnet build -c Release
copy bin\Release\net48\HamiiFolderLocker.exe pkg\HamiiFolderLocker\
mkdir build-payload && copy pkg\HamiiFolderLocker\* build-payload\
(cd build-payload && zip ../setup-boot/payload.zip *)
dotnet build setup-boot/DarkBoxSetup.csproj -c Release
```
