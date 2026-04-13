# 🚀 QUICK START - AUTOMATED SETUP

## 3-Step Startup

### 1️⃣ **Fix Compilation** (one-time setup)

If you see `OpenAIClient` not found error:

```bash
# Try full rebuild in VS Code
Ctrl+Shift+B  # Or Cmd+Shift+B on Mac

# Or from terminal:
cd src/FindDoctor.Web
dotnet clean
dotnet build
```

If still failing → use **Visual Studio** (full IDE, not VS Code)

### 2️⃣ **Run the App**

```bash
cd src/FindDoctor.Web
dotnet run
```

You'll see:
```
🔄 Starting automatic data sync from blob storage...
✅ Data sync completed successfully
```

### 3️⃣ **Open Chat**

```
https://localhost:5001
```

**That's it!** No /api/ingest calls needed. Data syncs automatically.

---

##What's Configured

✅ Blob storage connection string  
✅ Search index admin key  
✅ Automatic sync on startup  
✅ Incremental updates (no duplicates)  

All in `appsettings.json` - ready to go!

---

## Try These Queries

```
"dermatologist near Cleveland"
"cardiologist with online scheduling"
"female doctor for heart issues"
"spanish speaking pediatrician"
```

---

## Need Help?

See `AUTOMATION_GUIDE.md` for detailed setup  
See `AUTOMATION_ARCHITECTURE.md` for how it works

