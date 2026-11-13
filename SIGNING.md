# DarkBox — Windows Defender / SmartScreen (v4.1.0)

## Seedhi baat (honest limit)

**Bina code-signing certificate ke SmartScreen ko 100% khatam nahi kiya ja
sakta.** Ye Windows ka design hai, DarkBox ka bug nahi:

- Jo bhi naya, unsigned EXE pehli dafa kisi naye PC par chalta hai, Windows
  usay "unknown publisher" samajhta hai aur blue screen dikhata hai:
  **"Windows protected your PC" → More info → Run anyway**.
- Ye screen sirf tab gayab hoti hai jab EXE par **trusted certificate se
  signature** ho aur us signature ki **reputation** ban jaye (hazaroon
  installs / waqt).
- Koi code change, manifest, ya installer tweak is screen ko bypass nahi
  kar sakta — jo website/tool ye dawa kare, wo jhoot bol rahi hai ya
  Windows ko dhoka dene ki koshish hai (aur Defender usay malware samajhta hai).

## v4.1.0 mein kya fix kiya gaya (jo code se ho sakta tha)

1. **Poori publisher identity** — `HamiiFolderLocker.csproj` mein ab:
   Company = Hamii Scripts, Product = DarkBox by Hamii,
   Copyright © 2026 Hamii Scripts, Description, AssemblyTitle.
   Is ke baghair signed build bhi "anonymous" dikhta hai.
2. **`src/app.manifest`** — `asInvoker` (admin nahi mangta — admin mangna
   khud ek red flag hai), Windows 10/11 compatibility, PerMonitorV2 DPI,
   long paths. DarkBox ko admin ki zaroorat hi nahi hai.
3. **Setup bootstrapper bhi identified** — `setup-boot` EXE par bhi wohi
   identity honi chahiye (neeche sign step dono EXE par chalta hai).

## Permanent fix — certificate (ek dafa ka kaam)

1. **Certificate khareedein** (Hamid ke naam / Hamii Scripts par):
   - Standard Code Signing (Sectigo/Comodo): ~$70–100/saal. Reputation
     dheere banti hai — pehle kuch haftay kuch users ko phir bhi warning
     mil sakti hai, phir khatam ho jati hai.
   - EV Code Signing: ~$200–300/saal. **Instant reputation** — SmartScreen
     warning foran khatam. Agar budget ho to yehi lein.
   - Kahan se: Sectigo, DigiCert, SSL.com. USB token par aata hai (EV).
2. **Sign karein** (Windows SDK ka `signtool.exe` — Visual Studio ke saath aata hai):
   ```bat
   sign.bat
   ```
   Ye script `DarkBox-Setup.exe` aur `HamiiFolderLocker.exe` dono ko sign
   karti hai (neeche dekhein — apna certificate thumbprint/certificate file
   daalein, phir chalaiyein).
3. **Microsoft ko submit karein** (muft, ek dafa):
   - https://www.microsoft.com/en-us/wdsi/filesubmission — signed setup upload
     karke "I am the publisher, this file is safe" declare karein.
   - Is se Defender ki false-positive reputation banti hai.
4. **Har release par dobara sign karein** — build ke baad `sign.bat`
   chalana release checklist ka hissa hai. Bina sign ke bheja hua update
   phir se warning dikhayega.

## Naye user ko warning aaye to kya kare (jab tak certificate na ho)

Ye steps user ko setup ke saath bhej dein — ye Windows ka official rasta hai,
koi hack nahi:

1. Blue screen par **"More info"** dabayein.
2. Neeche **"Run anyway"** dabayein.
3. Bas ek dafa — us PC par dobara nahi poochhega.

> Note: DarkBox ko "Run as administrator" karke chalane se SmartScreen
> khatam nahi hota — ulta UAC prompt barh jata hai. Normal double-click hi
> sahi tareeqa hai.
