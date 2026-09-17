
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Delegates;
using StardewValley.Menus;
using StardewValley.Triggers;

namespace MaisarahServiceMenu
{
    public class ModEntry : Mod
    {
        private class PendingEvent
        {
            public string ActionString = "";
            public int TicksToWait = 1;
        }

        // Semua path Portrait/Sprite yang kepake di Appearance entries Maisarah (content.json),
        // di-preload pas save di-load biar nggak ada delay ~1 detik pas Content Patcher
        // pertama kali nge-load asset itu (ini yang bikin portrait sempet keliatan "default/vanilla"
        // abis event+warp, sebelum versi Work_Saloon-nya kelar ke-load).
        private static readonly string[] AppearanceAssetsToPreload = new[]
        {
            "Portraits/Maisarah",
            "Characters/Maisarah",
            "Portraits/Maisarah_Spring",
            "Characters/Maisarah_Spring",
            "Portraits/Maisarah_Fall",
            "Characters/Maisarah_Fall",
            "Portraits/Maisarah_Winter",
            "Characters/Maisarah_Winter",
            "Portraits/Maisarah_Beach",
            "Characters/Maisarah_Beach",
            "Portraits/Maisarah_Work_Saloon",
            "Characters/Maisarah_Work_Saloon",
            "Portraits/Maisarah_Work_Manor",
            "Characters/Maisarah_Work_Manor",
            "Characters/MaiDoggy", // dipake appearance "MaiDoggy" (IS_EVENT 112233)
        };

        private readonly List<PendingEvent> pendingEvents = new();
        private int shopOpenDelay = -1; // -1 = nggak ada yang pending

        // nunggu player nutup dialogue reaksi SEBELUM event mulai
        private bool waitingForPreEventDialogueClose = false;
        private string pendingPreEventActionString = "";

        // nunggu event beneran MULAI dulu (CurrentEvent jadi non-null), baru abis itu pindah ke "nunggu selesai"
        private bool waitingForEventToStart = false;

        // nunggu event selesai (Game1.CurrentEvent balik null) buat munculin emote abis service
        private bool waitingForEventToFinish = false;
        private int postEventEmoteDelay = -1; // buffer abis CurrentEvent null, biar nggak numpuk sama fade/warp

        // Track lokasi Mai sendiri (bukan player) - dipake buat deteksi kapan DIA pindah
        // lewat schedule-nya sendiri, biar reloadSprite() ke-trigger di situ juga,
        // bukan cuma pas player yang warp ke Saloon.
        private string? lastKnownMaisarahLocation = null;

        // Second-chance reload: kalau reloadSprite() PERSIS pas momen transisi lokasi
        // kejeblos race condition (appearance salah resolve karena posisi/map belum
        // bener2 settle), ini bakal nge-reload LAGI beberapa tick kemudian buat nangkep
        // kondisi yang udah settle.
        private int pendingAppearanceRecheckTicks = -1;

        // Deteksi generik "ada EVENT APAPUN yang barusan kelar" (bukan cuma event kita
        // sendiri) - event NPC lain yang lewat Town/dkk bisa ikut nge-reset portrait/sprite
        // Mai walau lokasinya sendiri nggak berubah nama, jadi polling lokasi di atas
        // nggak nangkep ini. Ini nutup celah itu.
        private bool wasAnyEventRunningLastTick = false;

        public override void Entry(IModHelper helper)
        {
            this.Monitor.Log("Maisarah Service Menu mod loaded!", LogLevel.Info);

            TriggerActionManager.RegisterAction("yudhasocii.MaisarahServiceMenu_OpenShop", this.OpenMaisarahShop);
            TriggerActionManager.RegisterAction("yudhasocii.MaisarahServiceMenu_TriggerBasicEvent", this.TriggerBasicEvent);
            TriggerActionManager.RegisterAction("yudhasocii.MaisarahServiceMenu_TriggerPremiumEvent", this.TriggerPremiumEvent);

            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;

            // Preload semua varian Portrait/Sprite Maisarah pas save baru kebuka -
            // di titik ini Content Patcher udah selesai apply semua Load/EditData patch-nya,
            // jadi aman buat langsung force-load asset-asset ini ke content cache SMAPI.
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;

            // ROOT CAUSE KETEMU: appearance Maisarah defaultnya nyangkut ke entry seasonal
            // (misal "Spring") walaupun dia lagi di Saloon, dan CUMA kebenerin kalau
            // reloadSprite() dipanggil manual. Jadi kita paksa reload appearance-nya
            // di titik2 ini biar dia nggak pernah "nyangkut" di appearance yang salah:
            helper.Events.Player.Warped += this.OnPlayerWarped;
            helper.Events.GameLoop.DayStarted += this.OnDayStarted;

            // DIAGNOSTIC: log state asli Maisarah (Portrait ref, LastAppearanceId, lokasi)
            // PERSIS pas dialogue box dia beneran kebuka - biar kita tau ground-truth-nya,
            // bukan nebak dari log asset loading lagi.
            helper.Events.Display.MenuChanged += this.OnMenuChanged;
        }

        private void OnPlayerWarped(object? sender, WarpedEventArgs e)
        {
            // Digeneralisir - sebelumnya cuma ngecek "Saloon" doang, sekarang reload
            // appearance-nya kapan pun player warp ke lokasi yang SAMA kayak Mai lagi
            // berada (nutup kasus "farmer nyampe duluan/nyusul" buat Beach/ManorHouse juga,
            // bukan cuma Saloon).
            NPC? maisarah = Game1.getCharacterFromName("Maisarah");
            if (maisarah != null && e.NewLocation?.Name == maisarah.currentLocation?.Name)
            {
                maisarah.reloadSprite(true);
            }
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            Game1.getCharacterFromName("Maisarah")?.reloadSprite(true);
        }

        private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
        {
            if (e.NewMenu is DialogueBox dialogueBox)
            {
                NPC? speaker = dialogueBox.characterDialogue?.speaker;

                if (speaker != null && speaker.Name == "Maisarah")
                {
                    string portraitInfo = speaker.Portrait == null
                        ? "NULL"
                        : $"{speaker.Portrait.Width}x{speaker.Portrait.Height} (hash {speaker.Portrait.GetHashCode()})";

                    this.Monitor.Log(
                        $"[DIAG] Dialogue box opened for Maisarah. Portrait={portraitInfo}, " +
                        $"LastAppearanceId={speaker.LastAppearanceId ?? "null"}, " +
                        $"currentLocation={speaker.currentLocation?.Name ?? "null"}",
                        LogLevel.Debug);
                }
            }
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            foreach (string assetName in AppearanceAssetsToPreload)
            {
                try
                {
                    this.Helper.GameContent.Load<Texture2D>(assetName);
                }
                catch (System.Exception ex)
                {
                    // Kalau ada varian yang emang nggak dipake/nggak ke-generate (misal WorkOutfitEnabled=false),
                    // ini bakal gagal load - itu normal, tinggal di-skip aja, bukan error fatal.
                    this.Monitor.Log($"Skipped preloading '{assetName}' (probably not generated/enabled): {ex.Message}", LogLevel.Trace);
                }
            }

            this.Monitor.Log("Preloaded Maisarah appearance portraits/sprites.", LogLevel.Debug);
        }

        private bool OpenMaisarahShop(string[] args, TriggerActionContext context, out string error)
        {
            error = "";
            this.shopOpenDelay = 90; // ~1.5 detik, biar dialogue box sempet keliatan dulu
            return true;
        }

        private bool TriggerBasicEvent(string[] args, TriggerActionContext context, out string error)
        {
            error = "";
            this.Monitor.Log("Basic service purchased!", LogLevel.Debug);
            this.ClearHeldItem();
            Game1.player.applyBuff("maisarah_Loosened");
            this.StartPreEventReaction("PreServiceBasic", "spacechase0.SpaceCore_PlayEvent 958600");
            return true;
        }

        private bool TriggerPremiumEvent(string[] args, TriggerActionContext context, out string error)
        {
            error = "";
            this.Monitor.Log("Premium service purchased!", LogLevel.Debug);
            this.ClearHeldItem();
            Game1.player.applyBuff("maisarah_Wrecked");
            this.StartPreEventReaction("PreServicePremium", "spacechase0.SpaceCore_PlayEvent 958600");
            return true;
        }

        private void ClearHeldItem()
        {
            if (Game1.activeClickableMenu is ShopMenu shopMenu)
            {
                var heldItemField = this.Helper.Reflection.GetField<ISalable>(shopMenu, "heldItem");
                heldItemField.SetValue(null);
                this.Monitor.Log("Cleared held item from shop menu.", LogLevel.Debug);
            }
        }

        /// <summary>Tutup shop, tampilin dialogue reaksi Maisarah, dan tunda action event
        /// sampai player beneran nutup dialogue box-nya (bukan fixed delay).</summary>
        private void StartPreEventReaction(string dialogueKey, string eventActionString)
        {
            Game1.activeClickableMenu?.exitThisMenu();

            NPC? maisarah = Game1.getCharacterFromName("Maisarah");
            if (maisarah != null)
            {
                Dialogue dialogue = new Dialogue(maisarah, $"Characters/Dialogue/Maisarah:{dialogueKey}");
                maisarah.CurrentDialogue.Push(dialogue);
                Game1.drawDialogue(maisarah);
            }

            this.waitingForPreEventDialogueClose = true;
            this.pendingPreEventActionString = eventActionString;
        }

        private void QueueEvent(string actionString)
        {
            this.pendingEvents.Add(new PendingEvent { ActionString = actionString, TicksToWait = 1 });
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            // 0) Deteksi Mai PINDAH LOKASI SENDIRI lewat schedule-nya (bukan cuma player yang warp) -
            //    tiap kali lokasinya beda dari yang terakhir ke-cek, paksa reload appearance-nya
            //    di situ juga. Ini nutup celah yang kelewat sebelumnya (reload cuma ke-trigger
            //    pas PLAYER warp ke Saloon, nggak ke-trigger pas Mai sendiri pindah ke lokasi lain
            //    kayak Town lewat schedule).
            NPC? maisarahForLocationCheck = Game1.getCharacterFromName("Maisarah");
            string? currentMaisarahLocation = maisarahForLocationCheck?.currentLocation?.Name;
            if (currentMaisarahLocation != this.lastKnownMaisarahLocation)
            {
                this.lastKnownMaisarahLocation = currentMaisarahLocation;
                maisarahForLocationCheck?.reloadSprite(true);
                this.pendingAppearanceRecheckTicks = 30; // ~0.5 detik - second chance kalau attempt pertama kejeblos race condition
            }

            // Second-chance reload: nembak lagi reloadSprite beberapa tick abis transisi,
            // buat nangkep kondisi yang udah settle kalau attempt pertama (di atas) telat/salah resolve.
            if (this.pendingAppearanceRecheckTicks > 0)
            {
                this.pendingAppearanceRecheckTicks--;
                if (this.pendingAppearanceRecheckTicks == 0)
                {
                    Game1.getCharacterFromName("Maisarah")?.reloadSprite(true);
                }
            }

            // 0b) Deteksi "ada EVENT APAPUN yang barusan kelar" (bukan cuma event kita sendiri,
            //     termasuk event NPC lain yang lewat/di-skip). Event manapun bisa ikut ngerusak
            //     state portrait/sprite Mai walau lokasinya nggak berubah nama, jadi poll di atas
            //     nggak nangkep - ini jaring pengaman tambahan buat kasus itu.
            bool isAnyEventRunningNow = Game1.CurrentEvent != null || Game1.eventUp;
            if (this.wasAnyEventRunningLastTick && !isAnyEventRunningNow)
            {
                Game1.getCharacterFromName("Maisarah")?.reloadSprite(true);
                this.pendingAppearanceRecheckTicks = 30; // second chance juga buat kasus ini
            }
            this.wasAnyEventRunningLastTick = isAnyEventRunningNow;

            // 1) Delay buka shop abis $action dialogue Saloon
            if (this.shopOpenDelay > 0)
            {
                this.shopOpenDelay--;
                if (this.shopOpenDelay == 0)
                {
                    Utility.TryOpenShopMenu("yudhasocii.MaisarahNPC_ServiceShop", "Maisarah");
                    this.shopOpenDelay = -1;
                }
            }

            // 2) Nunggu dialogue reaksi PRE-event ditutup player, baru start event-nya
            if (this.waitingForPreEventDialogueClose)
            {
                bool dialogueStillUp = Game1.activeClickableMenu is DialogueBox || Game1.dialogueUp;
                if (!dialogueStillUp)
                {
                    this.waitingForPreEventDialogueClose = false;
                    this.waitingForEventToStart = true; // tunggu event beneran mulai dulu
                    this.QueueEvent(this.pendingPreEventActionString);
                }
            }

            // 2b) Begitu event beneran mulai (CurrentEvent non-null), baru pindah ke mode "nunggu selesai"
            if (this.waitingForEventToStart && (Game1.CurrentEvent != null || Game1.eventUp))
            {
                this.waitingForEventToStart = false;
                this.waitingForEventToFinish = true;
            }

            // 3) Deteksi event yang lagi jalan udah kelar (CurrentEvent balik null),
            //    tapi kasih buffer dulu biar nggak numpuk sama warp/fade abis event
            if (this.waitingForEventToFinish && Game1.CurrentEvent == null && !Game1.eventUp)
            {
                this.waitingForEventToFinish = false;
                this.postEventEmoteDelay = 40; // ~0.66 detik, sesuaikan kalau masih numpuk/kelamaan
            }

            if (this.postEventEmoteDelay > 0)
            {
                this.postEventEmoteDelay--;
                if (this.postEventEmoteDelay == 0)
                {
                    NPC? maisarah = Game1.getCharacterFromName("Maisarah");

                    // Maksa dia re-evaluate Appearance (Data/Characters) sekarang juga,
                    // biar Portrait/Sprite field-nya udah ke-assign ulang yang bener (Work_Saloon dkk)
                    // SEBELUM player sempet buka dialog lagi abis warp. Ini beda sama preload asset -
                    // preload cuma nyiapin file-nya di cache, reloadSprite ini yang beneran maksa
                    // reassignment field Portrait/Sprite si NPC-nya sendiri.
                    // onlyAppearance: true -> nggak ganggu data lain (posisi, schedule, dst).
                    maisarah?.reloadSprite(true);

                    if (maisarah != null)
                    {
                        string portraitInfo = maisarah.Portrait == null
                            ? "NULL"
                            : $"{maisarah.Portrait.Width}x{maisarah.Portrait.Height} (hash {maisarah.Portrait.GetHashCode()})";
                        this.Monitor.Log(
                            $"[DIAG] Abis reloadSprite: Portrait={portraitInfo}, LastAppearanceId={maisarah.LastAppearanceId ?? "null"}, currentLocation={maisarah.currentLocation?.Name ?? "null"}",
                            LogLevel.Debug);
                    }

                    maisarah?.doEmote(20); // heart
                }
            }

            // 4) Proses pending action (start event, dst) — tetap sama kayak sebelumnya
            for (int i = this.pendingEvents.Count - 1; i >= 0; i--)
            {
                var pending = this.pendingEvents[i];
                pending.TicksToWait--;

                if (pending.TicksToWait <= 0)
                {
                    if (!TriggerActionManager.TryRunAction(pending.ActionString, out string error, out System.Exception? ex))
                    {
                        this.Monitor.Log($"Failed running delayed action '{pending.ActionString}': {error}", LogLevel.Error);
                    }

                    this.pendingEvents.RemoveAt(i);
                }
            }
        }
    }
}
