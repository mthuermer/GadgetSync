using System.Collections;
using System.Collections.Generic;
using System.IO;
using DV.CabControls;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using DV.InventorySystem;
using DV.Utils;
using HarmonyLib;
using MPAPI;
using MPAPI.Interfaces;
using MPAPI.Interfaces.Packets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GadgetMirror
{
    /// <summary>
    /// Client -> Host: Gadget anbringen (Op 1) oder abbauen (Op 2). Lage lokal zum Wagen.
    /// Host -> Client: Antwort mit derselben Id; beim Abbauen mit dem Gegenstand, der beim Client landet.
    /// </summary>
    public class GadgetBuildPacket : ISerializablePacket
    {
        public const byte PLACE = 1, REMOVE = 2, RESULT = 3, ACK = 4, SOUND = 5;   // ACK: ATS quittiert; SOUND: Ton bei den anderen

        public byte Op;
        public int Id;
        public bool Ok;
        public string Message = string.Empty;
        public string CarGuid = string.Empty;
        public string Prefab = string.Empty;
        public int Uid;        // Abbauen: welches Gadget; Anbringen: Halterung (0 = keine)
        public Vector3 Pos;
        public Quaternion Rot = Quaternion.identity;
        public string Data = "{}";

        public void Serialize(BinaryWriter w)
        {
            w.Write(Op); w.Write(Id); w.Write(Ok); w.Write(Message ?? string.Empty);
            w.Write(CarGuid ?? string.Empty); w.Write(Prefab ?? string.Empty); w.Write(Uid);
            w.Write(Pos.x); w.Write(Pos.y); w.Write(Pos.z);
            w.Write(Rot.x); w.Write(Rot.y); w.Write(Rot.z); w.Write(Rot.w);
            w.Write(Data ?? "{}");
        }

        public void Deserialize(BinaryReader r)
        {
            Op = r.ReadByte(); Id = r.ReadInt32(); Ok = r.ReadBoolean(); Message = r.ReadString();
            CarGuid = r.ReadString(); Prefab = r.ReadString(); Uid = r.ReadInt32();
            Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            Rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            Data = r.ReadString();
        }
    }

    /// <summary>Anbringen beim Client: statt selbst zu bauen, den Host bitten.</summary>
    [HarmonyPatch(typeof(GadgetItem), nameof(GadgetItem.TryPlace))]
    internal static class TryPlacePatch
    {
        private static bool Prefix(Customization destination, Vector3 position, Quaternion rotation, GadgetItem gadgetItem,
                                   Collider colliderForPlacementData, bool instantiateGadget, ref GadgetBase gadget, ref bool __result)
        {
            Mirror mirror = Mirror.Instance;
            if (mirror == null || !mirror.BuildsViaHost(destination)) return true;
            mirror.RequestPlace(destination, position, rotation, gadgetItem, colliderForPlacementData, instantiateGadget);
            gadget = null;
            __result = false;
            return false;
        }
    }

    /// <summary>Abbauen beim Client: Spiegel-Gadgets baut der Host ab.</summary>
    [HarmonyPatch(typeof(GadgetBase), nameof(GadgetBase.Remove))]
    internal static class RemovePatch
    {
        private static bool Prefix(GadgetBase __instance, ref GadgetItem __result)
        {
            Mirror mirror = Mirror.Instance;
            if (mirror != null) mirror.HostChanged(__instance);   // beim Host: Wagen sofort neu senden
            if (mirror == null || GameRemoves.Active > 0 || !mirror.IsMirror(__instance)) return true;
            mirror.RequestRemove(__instance);
            __result = null;
            return false;
        }
    }

    /// <summary>
    /// Das Spiel baut auch selbst ab: Wagen geloescht, lose Gadgets fallen beim Stoss ab, Scheibe
    /// zerbricht. Das entscheidet beim Host seine eigene Physik -- beim Client nur lokal, nie anfragen.
    /// </summary>
    [HarmonyPatch]
    internal static class GameRemoves
    {
        internal static int Active;

        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GadgetBase), "OnTrainCarDestroyed");
            yield return AccessTools.Method(typeof(GadgetBase), "OnGlassBroken");
            yield return AccessTools.Method(typeof(Drillable), "OnGlassBroken");
            yield return AccessTools.Method(typeof(TrainCarCustomization), "OnCollisionEnter");
        }

        private static void Prefix() { Active++; }

        private static System.Exception Finalizer(System.Exception __exception)
        {
            Active--;
            return __exception;
        }
    }

    /// <summary>
    /// Schrauben, Kleben, Loeten, Verkabeln am Spiegel: der Client tut es ganz normal lokal (sieht es
    /// sofort, Werkzeug und Verbrauch laufen wie gewohnt), der Delta-Weg meldet die Aenderung dem Host.
    /// Loeten laeuft Frame fuer Frame -- einzelne Anfragen waeren zu viele und kaemen zu spaet.
    /// </summary>
    [HarmonyPatch(typeof(Drillable), nameof(Drillable.SetMountPointState))]
    internal static class MountPointPatch
    {
        private static void Postfix(Drillable __instance)
        {
            if (Mirror.Instance != null) Mirror.Instance.Touched(__instance.GetComponent<GadgetBase>());
        }
    }

    /// <summary>
    /// Text geaendert (Eingabefenster geschlossen) -- ebenfalls ueber den Delta-Weg. Prefix: das Spiel
    /// wirft in PopupClosed selbst eine NullReferenceException (auch ohne Mods, nachdem der Text
    /// gesetzt ist); alles danach -- Postfix, weitere Fenster-Handler -- liefe nie. Gesendet wird erst
    /// einen Takt spaeter, dann steht der Text.
    /// </summary>
    [HarmonyPatch(typeof(TextGadget), "PopupClosed")]
    internal static class TextPatch
    {
        private static void Prefix(TextGadget __instance)
        {
            if (Mirror.Instance != null) Mirror.Instance.Touched(__instance.GetComponentInParent<GadgetBase>());
        }
    }

    [HarmonyPatch(typeof(TrainCarCustomization.TrainCarCustomizerBase), nameof(TrainCarCustomization.TrainCarCustomizerBase.MakeSoldered))]
    internal static class SolderPatch
    {
        private static void Postfix(TrainCarCustomization.TrainCarCustomizerBase __instance)
        {
            if (Mirror.Instance != null) Mirror.Instance.Touched(__instance as GadgetBase);
        }
    }

    [HarmonyPatch(typeof(TrainCarCustomization.TrainCarCustomizerBase), nameof(TrainCarCustomization.TrainCarCustomizerBase.RemoveSoldering))]
    internal static class DesolderPatch
    {
        private static void Postfix(TrainCarCustomization.TrainCarCustomizerBase __instance)
        {
            if (Mirror.Instance != null) Mirror.Instance.Touched(__instance as GadgetBase);
        }
    }

    [HarmonyPatch(typeof(GadgetWiringModule.WireLinkPort), nameof(GadgetWiringModule.WireLinkPort.Wire))]
    internal static class WirePatch
    {
        private static void Postfix(GadgetWiringModule.WireLinkPort a, GadgetWiringModule.WireLinkPort b, bool __result)
        {
            if (!__result || Mirror.Instance == null) return;
            Mirror.Instance.Touched(a.owner);
            Mirror.Instance.Touched(b.owner);
        }
    }

    /// <summary>Auch Unwire(port) -- "alle Kabel ab" -- laeuft ueber dieses Unwire(a, b).</summary>
    [HarmonyPatch(typeof(GadgetWiringModule.WireLinkPort), nameof(GadgetWiringModule.WireLinkPort.Unwire),
                  new[] { typeof(GadgetWiringModule.WireLinkPort), typeof(GadgetWiringModule.WireLinkPort) })]
    internal static class UnwirePatch
    {
        private static void Postfix(GadgetWiringModule.WireLinkPort a, GadgetWiringModule.WireLinkPort b, bool __result)
        {
            if (!__result || Mirror.Instance == null) return;
            Mirror.Instance.Touched(a.owner);
            Mirror.Instance.Touched(b.owner);
        }
    }

    /// <summary>
    /// Beim Host bewegt sich ein Bedienelement (Spieler, Gadget-Logik, Uebernahme eines Pushes): gehoert es
    /// zu einem Gadget, geht der Wagen sofort raus statt erst beim Sekunden-Abgleich.
    /// </summary>
    [HarmonyPatch]
    internal static class ControlChangedPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ControlImplBase), nameof(ControlImplBase.SetValue));
            yield return AccessTools.Method(typeof(ControlImplBase), "RequestValueUpdate");
            yield return AccessTools.Method(typeof(ControlImplBase), nameof(ControlImplBase.Use));
            yield return AccessTools.Method(typeof(ControlImplBase), nameof(ControlImplBase.UnUse));
        }

        private static void Postfix(ControlImplBase __instance)
        {
            if (Mirror.Instance != null) Mirror.Instance.ControlChanged(__instance);
        }
    }

    /// <summary>Beim Host angebracht (selbst oder fuer einen Client): Wagen sofort senden.</summary>
    [HarmonyPatch(typeof(GadgetItem), nameof(GadgetItem.Place))]
    internal static class PlacedPatch
    {
        private static void Postfix(GadgetBase __result)
        {
            if (Mirror.Instance != null) Mirror.Instance.HostChanged(__result);
        }
    }

    /// <summary>
    /// SwitchSetter: das Spiel spielt den Ton nur beim Druecker. Die Weiche selbst gleicht die MP-Mod ab,
    /// den Ton geben wir weiter -- abgespielt wird er dort, wo der Spiegel bzw. das Gadget sein Modell hat.
    /// </summary>
    [HarmonyPatch(typeof(GadgetSwitchSetterLOD), nameof(GadgetSwitchSetterLOD.BtnChange))]
    internal static class SwitchSetterSoundPatch
    {
        private static void Postfix(GadgetSwitchSetterLOD __instance)
        {
            if (Mirror.Instance == null || __instance.Base == null || !__instance.Base.PowerState) return;
            if (Traverse.Create(__instance).Field("currentJunction").GetValue() == null) return;   // nichts geschaltet
            Mirror.Instance.ForwardSound(__instance.Base);
        }
    }

    /// <summary>
    /// Sim-Gadgets (ATS, Schleuder-, Ueberhitzungsschutz) greifen in ihrem Update in die Lok-Hebel ein. Beim
    /// Client laeuft dafuer der Spiegel mit -- seine Eingriffe gingen als Eingabe des Clients an den Host:
    /// doppelt, zu eigener Zeit, und dem Spieler wird der Hebel aus der Hand genommen. Eingreifen darf nur
    /// das echte Gadget beim Host; der Spiegel rechnet weiter (Anzeigen, Warnlampe), bewegt aber nichts.
    /// </summary>
    [HarmonyPatch]
    internal static class SimMirrorUpdate
    {
        internal static int Active;

        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GadgetATS), "Update");
            yield return AccessTools.Method(typeof(GadgetAntiSlip), "Update");
            yield return AccessTools.Method(typeof(GadgetOverheatProtection), "Update");
        }

        private static void Prefix(GadgetBase __instance, out bool __state)
        {
            __state = Mirror.Instance != null && Mirror.Instance.IsMirror(__instance);
            if (__state) Active++;
        }

        private static System.Exception Finalizer(System.Exception __exception, bool __state)
        {
            if (__state) Active--;
            return __exception;
        }
    }

    /// <summary>
    /// Quittieren am ATS setzt nur dessen Zeitgeber zurueck -- der steht in keinem Spielstand. Am Spiegel
    /// quittiert, muss es der Host am echten ATS nachholen, sonst bremst es trotzdem.
    /// </summary>
    [HarmonyPatch(typeof(GadgetATSLOD), "OnAckButton")]
    internal static class AtsAckPatch
    {
        private static void Postfix(GadgetATSLOD __instance)
        {
            if (Mirror.Instance != null) Mirror.Instance.ForwardAck(__instance.Base);
        }
    }

    [HarmonyPatch]
    internal static class SimMirrorHands
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(DV.Simulation.Controllers.OverridableBaseControl), "Set");
            yield return AccessTools.Method(typeof(DV.Simulation.Controllers.OverridableBaseControl), "Move");
            yield return AccessTools.Method(typeof(DV.Simulation.Controllers.PowerOffControl), "Set");
            yield return AccessTools.Method(typeof(GadgetATS), "UnhandControl");
            yield return AccessTools.Method(typeof(DV.HUD.InteriorControlsManager), "TryUnhandControl");
        }

        private static bool Prefix() { return SimMirrorUpdate.Active == 0; }
    }

    /// <summary>
    /// Bauen durch Clients. Wer baut, fuehrt nicht selbst aus: der Client bittet den Host, der
    /// baut mit denselben Spielfunktionen und verteilt das Ergebnis wie jede Aenderung.
    /// Gegenstaende synchronisiert die MP-Mod nicht -- der Gegenstand gehoert deshalb dem, der baut:
    /// beim Anbringen verschwindet er erst nach Zusage des Hosts aus seiner Hand, beim Abbauen
    /// landet er bei ihm in der Kabine (der Host entsorgt seinen).
    /// </summary>
    public partial class Mirror
    {
        private const float BUILD_TIMEOUT_SECONDS = 10f;

        internal static Mirror Instance;

        private class BuildRequest
        {
            public byte Op;
            public GadgetItem Item;      // wird nach Zusage verbraucht; null = nichts zu verbrauchen
            public bool AskText;         // Text-Gadget: Eingabefenster am neuen Spiegel oeffnen
            public float Time;
        }

        // Client: offene Anfragen
        private readonly Dictionary<int, BuildRequest> building = new Dictionary<int, BuildRequest>();
        // Client: neu angebrachte Text-Gadgets (Wagen, UID, Zeitpunkt), deren Spiegel noch das Eingabefenster braucht
        private readonly List<KeyValuePair<KeyValuePair<string, int>, float>> askText = new List<KeyValuePair<KeyValuePair<string, int>, float>>();
        private int lastBuildId;

        internal bool BuildsViaHost(Customization destination)
        {
            return MultiplayerAPI.Client != null && MultiplayerAPI.Instance != null && !MultiplayerAPI.Instance.IsHost
                   && destination is TrainCarCustomization;
        }

        /// <summary>Spieler hat ein Spiegel-Gadget veraendert (nicht wir beim Anwenden): an den Host melden.</summary>
        internal void Touched(GadgetBase gadget)
        {
            if (GadgetState.Applying > 0 || gadget == null) return;
            if (MultiplayerAPI.Server != null)
            {
                HostChanged(gadget);   // beim Host geschraubt, geloetet, verkabelt, beschriftet: sofort an alle
                return;
            }
            if (!IsMirror(gadget)) return;
            TrainCarCustomization custom = gadget.Custom as TrainCarCustomization;
            if (custom != null) MarkDirty(gadget, custom.TrainCar.CarGUID);
        }

        internal void ForwardSound(GadgetBase gadget)
        {
            TrainCarCustomization custom = gadget.Custom as TrainCarCustomization;
            if (custom == null) return;
            GadgetBuildPacket packet = new GadgetBuildPacket { Op = GadgetBuildPacket.SOUND, CarGuid = custom.TrainCar.CarGUID, Uid = gadget.UID };
            if (MultiplayerAPI.Server != null) MultiplayerAPI.Server.SendSerializablePacketToAll(packet, true, true);
            else if (hostCompatible && IsMirror(gadget)) MultiplayerAPI.Client.SendSerializablePacketToServer(packet);
        }

        private static void PlaySound(string carGuid, int uid)
        {
            Customization custom;
            TrainCarCustomization.TrainCarCustomizerBase found;
            GadgetBase gadget = Customization.TryGetFromIdentificationKey(carGuid, out custom) && custom != null &&
                                custom.TryGetCustomizerByUID(uid, out found) ? found as GadgetBase : null;
            GadgetSwitchSetterLOD lod = gadget != null ? gadget.GetComponentInChildren<GadgetSwitchSetterLOD>() : null;
            if (lod != null && lod.switchChangeAudio != null)   // wie in BtnChange
                lod.switchChangeAudio.Play(lod.transform.position, 1f, 1f, 0f, 1f, 10f, default(AudioSourceCurves), null, lod.transform);
        }

        internal void ForwardAck(GadgetATS ats)
        {
            TrainCarCustomization custom = ats != null ? ats.Custom as TrainCarCustomization : null;
            if (custom == null || !hostCompatible || !IsMirror(ats)) return;
            MultiplayerAPI.Client.SendSerializablePacketToServer(new GadgetBuildPacket
            {
                Op = GadgetBuildPacket.ACK, CarGuid = custom.TrainCar.CarGUID, Uid = ats.UID,
            });
        }

        internal bool IsMirror(GadgetBase gadget)
        {
            if (gadget == null || gadget.GadgetItem == null) return false;
            foreach (List<GameObject> spawned in mirrored.Values)
                if (spawned.Contains(gadget.GadgetItem.gameObject)) return true;
            return false;
        }

        // ---------------------------------------------------------------- Client

        /// <param name="copy">Das Spiel bringt eine Kopie an (Etikettendrucker): die Vorlage bleibt.</param>
        internal void RequestPlace(Customization destination, Vector3 position, Quaternion rotation, GadgetItem item, Collider onto, bool copy)
        {
            if (!CanAskHost()) return;
            foreach (BuildRequest open in building.Values)
                if (open.Item == item) return;   // wartet schon auf den Host -- kein zweites Mal

            Transform parent = destination.GetParentingTransform();
            GadgetBase target = onto != null ? onto.GetComponentInParent<GadgetBase>() : null;
            InventoryItemSpec spec = item.GetComponent<InventoryItemSpec>();
            GadgetBuildPacket packet = new GadgetBuildPacket
            {
                Op = GadgetBuildPacket.PLACE,
                Id = ++lastBuildId,
                CarGuid = ((TrainCarCustomization)destination).TrainCar.CarGUID,
                Prefab = spec != null ? spec.ItemPrefabName : string.Empty,
                Uid = target != null && target.GetComponent<Mount>() != null ? target.UID : 0,
                Pos = parent.InverseTransformPoint(position),
                Rot = Quaternion.Inverse(parent.rotation) * rotation,
                Data = Portable(GadgetState.Data(item.Gadget)).ToString(Formatting.None),
            };
            building[packet.Id] = new BuildRequest
            {
                Op = packet.Op, Item = copy ? null : item, Time = Time.time,
                AskText = item.Gadget != null && item.Gadget.GetComponentInChildren<TextGadget>() != null,
            };
            MultiplayerAPI.Client.SendSerializablePacketToServer(packet);
        }

        internal void RequestRemove(GadgetBase gadget)
        {
            if (!CanAskHost()) return;
            TrainCarCustomization custom = gadget.Custom as TrainCarCustomization;
            if (custom == null) return;
            GadgetBuildPacket packet = new GadgetBuildPacket
            {
                Op = GadgetBuildPacket.REMOVE,
                Id = ++lastBuildId,
                CarGuid = custom.TrainCar.CarGUID,
                Uid = gadget.UID,
            };
            building[packet.Id] = new BuildRequest { Op = packet.Op, Time = Time.time };
            MultiplayerAPI.Client.SendSerializablePacketToServer(packet);
        }

        private bool CanAskHost()
        {
            if (hostCompatible) return true;
            ChatLocal("GadgetMirror: Bauen geht nur, wenn Host und du dieselbe GadgetMirror-Version haben.");
            return false;
        }

        private void OnBuildResult(GadgetBuildPacket packet)
        {
            if (packet.Op == GadgetBuildPacket.SOUND)
            {
                PlaySound(packet.CarGuid, packet.Uid);
                return;
            }
            BuildRequest open;
            if (packet.Op != GadgetBuildPacket.RESULT || !building.TryGetValue(packet.Id, out open)) return;
            building.Remove(packet.Id);
            if (!packet.Ok)
            {
                ChatLocal("GadgetMirror: " + packet.Message);   // Gegenstand bleibt in der Hand
                return;
            }
            if (open.Op == GadgetBuildPacket.REMOVE)
            {
                StartCoroutine(SpawnRemovedItem(packet));
                return;
            }
            if (open.Item != null) Consume(open.Item);
            // Beim Anbringen fragt das Spiel nach dem Text -- beim Host gibt es niemanden, der antwortet.
            if (open.AskText) askText.Add(new KeyValuePair<KeyValuePair<string, int>, float>(
                new KeyValuePair<string, int>(packet.CarGuid, packet.Uid), Time.time));
        }

        /// <summary>Eingabefenster am Spiegel oeffnen, sobald der Spiegel des neuen Text-Gadgets da ist.</summary>
        private void AskForText()
        {
            for (int i = askText.Count - 1; i >= 0; i--)
            {
                Customization custom;
                TrainCarCustomization.TrainCarCustomizerBase found;
                GadgetBase gadget = Customization.TryGetFromIdentificationKey(askText[i].Key.Key, out custom) && custom != null &&
                                    custom.TryGetCustomizerByUID(askText[i].Key.Value, out found) ? found as GadgetBase : null;
                if (gadget != null && IsMirror(gadget))
                {
                    TextGadget text = gadget.GetComponentInChildren<TextGadget>();
                    if (text != null) text.ShowPopup(useExistingText: false, removeButton: false);
                }
                else if (Time.time - askText[i].Value < BUILD_TIMEOUT_SECONDS) continue;
                askText.RemoveAt(i);
            }
        }

        /// <summary>Antwortet der Host nicht, gibt die Anfrage den Gegenstand wieder frei.</summary>
        private void ExpireBuildRequests()
        {
            AskForText();
            List<int> stale = null;
            foreach (KeyValuePair<int, BuildRequest> entry in building)
                if (Time.time - entry.Value.Time > BUILD_TIMEOUT_SECONDS) (stale = stale ?? new List<int>()).Add(entry.Key);
            if (stale == null) return;
            foreach (int id in stale) building.Remove(id);
            ChatLocal("GadgetMirror: Der Host hat nicht geantwortet -- bitte noch einmal versuchen.");
        }

        /// <summary>Angebrachter Gegenstand: aus Hand bzw. Inventar nehmen und entsorgen -- der Host hat jetzt das Gadget.</summary>
        private static void Consume(GadgetItem item)
        {
            if (item == null) return;
            GameObject go = item.gameObject;
            if (item.Item != null)
            {
                item.Item.ForceEndInteraction();
                SingletonBehaviour<StorageController>.Instance.RemoveItemFromWorldStorage(item.Item);
            }
            SingletonBehaviour<Inventory>.Instance.DropItemFromHandsOrInventory(go);
            SingletonBehaviour<Inventory>.Instance.PurgeFromInventory(go);
            if (item.Gadget != null) Object.Destroy(item.Gadget.gameObject);
            Object.Destroy(go);
        }

        /// <summary>Abgebautes Teil liegt beim Client dort in der Kabine, wo das Gadget war -- wie im Spiel.</summary>
        private IEnumerator SpawnRemovedItem(GadgetBuildPacket packet)
        {
            Customization custom;
            GameObject prefab = FindItemPrefab(packet.Prefab);
            if (prefab == null || !Customization.TryGetFromIdentificationKey(packet.CarGuid, out custom) || custom == null) yield break;
            Transform parent = custom.GetParentingTransform();
            GameObject go = Object.Instantiate(prefab, parent.TransformPoint(packet.Pos), parent.rotation * packet.Rot);
            go.SetActive(true);
            yield return null;   // GadgetItem.Start() setzt .Item
            GadgetItem item = go.GetComponent<GadgetItem>();
            if (item == null || item.Item == null) yield break;
            item.Item.InventorySpecs.BelongsToPlayer = true;
            GadgetState.Merge(item.Gadget, JObject.Parse(packet.Data));
            SingletonBehaviour<StorageController>.Instance.AddItemToWorldStorage(item.Item);
            TrainCar car = ((TrainCarCustomization)custom).TrainCar;
            ItemReparentingBase reparenting = item.Item.GetComponent<ItemReparentingBase>();
            if (car != null && reparenting != null) reparenting.ParentItemExternal(car.interior, car.rb);
        }

        // ---------------------------------------------------------------- Host

        private void OnBuildRequest(GadgetBuildPacket packet, IPlayer sender)
        {
            if (packet.Op == GadgetBuildPacket.SOUND)
            {
                if (!compatible.Contains(sender.PlayerId)) return;
                PlaySound(packet.CarGuid, packet.Uid);
                MultiplayerAPI.Server.SendSerializablePacketToAll(packet, true, true, sender);   // und an die uebrigen Clients
                return;
            }
            Customization custom;
            string error = null;
            if (!compatible.Contains(sender.PlayerId)) error = "Bauen abgelehnt: andere GadgetMirror-Version.";
            else if (!Customization.TryGetFromIdentificationKey(packet.CarGuid, out custom) || !(custom is TrainCarCustomization))
                error = "Bauen abgelehnt: Wagen gibt es beim Host nicht.";
            else if (packet.Op == GadgetBuildPacket.PLACE) { StartCoroutine(PlaceForClient(packet, sender, custom)); return; }
            else if (packet.Op == GadgetBuildPacket.REMOVE) error = RemoveForClient(packet, sender, custom);
            else if (packet.Op == GadgetBuildPacket.ACK)
            {
                TrainCarCustomization.TrainCarCustomizerBase found;
                GadgetATS ats = custom.TryGetCustomizerByUID(packet.Uid, out found) ? found as GadgetATS : null;
                if (ats != null) ats.currentTimer = ats.CurrentRegimeTimerLength;   // wie GadgetATSLOD.OnAckButton
                return;
            }
            if (error != null) Reply(packet, sender, false, error);
        }

        private IEnumerator PlaceForClient(GadgetBuildPacket packet, IPlayer sender, Customization custom)
        {
            GameObject prefab = FindItemPrefab(packet.Prefab);
            if (prefab == null) { Reply(packet, sender, false, "Anbringen abgelehnt: unbekanntes Gadget " + packet.Prefab); yield break; }
            Mount mount = null;
            if (packet.Uid != 0)
            {
                TrainCarCustomization.TrainCarCustomizerBase onto;
                mount = custom.TryGetCustomizerByUID(packet.Uid, out onto) && onto is GadgetBase ? ((GadgetBase)onto).GetComponent<Mount>() : null;
                if (mount == null || mount.IsInUse) { Reply(packet, sender, false, "Anbringen abgelehnt: Halterung ist weg oder belegt."); yield break; }
            }

            GameObject go = Object.Instantiate(prefab, custom.transform.position + Vector3.up * 3f, Quaternion.identity);
            go.SetActive(true);
            yield return null;   // GadgetItem.Start() setzt .Item
            GadgetItem item = go.GetComponent<GadgetItem>();
            Transform parent = custom.GetParentingTransform();
            GadgetBase gadget = null;
            // Die Flaeche zaehlt nur fuer "auf Glas" -- dafuer muessen die Montageflaechen aktiv sein.
            CustomizationPlacementMeshes.EnsurePlacingMeshesAreActive();
            Collider surface = mount != null ? mount.GetComponentInChildren<Collider>() : custom.GetComponentInChildren<Collider>();
            if (item == null || item.Item == null ||
                !GadgetItem.TryPlace(custom, parent.TransformPoint(packet.Pos), parent.rotation * packet.Rot, item, surface, false, out gadget))
            {
                Object.Destroy(go);
                Reply(packet, sender, false, "Anbringen abgelehnt: kein Platz an dieser Stelle (beim Host).");
                yield break;
            }
            if (mount != null) mount.MountGadget(gadget);
            GadgetState.Merge(gadget, JObject.Parse(packet.Data));
            Reply(packet, sender, true, null, gadget.UID);
            SendIfChanged(((TrainCarCustomization)custom).TrainCar);
        }

        private string RemoveForClient(GadgetBuildPacket packet, IPlayer sender, Customization custom)
        {
            TrainCarCustomization.TrainCarCustomizerBase found;
            GadgetBase gadget = custom.TryGetCustomizerByUID(packet.Uid, out found) ? found as GadgetBase : null;
            if (gadget == null) return "Abbauen abgelehnt: das Gadget ist schon weg.";
            // Wie GadgetBase.Remove selbst: irgendein Weg muss erlaubt sein. Welcher (Hammer, Etikettendrucker,
            // Hand) hat schon das Werkzeug beim Client geprueft -- Etiketten etwa gehen nicht mit dem Hammer.
            if (!gadget.CanBeRemovedUsingMethod(GadgetBase.GadgetRemovalMethod.Any)) return "Abbauen abgelehnt: es sitzt noch etwas darauf.";

            Transform parent = custom.GetParentingTransform();
            InventoryItemSpec spec = gadget.GadgetItem != null ? gadget.GadgetItem.GetComponent<InventoryItemSpec>() : null;
            GadgetBuildPacket result = new GadgetBuildPacket
            {
                Prefab = spec != null ? spec.ItemPrefabName : string.Empty,
                Pos = parent.InverseTransformPoint(gadget.transform.position),
                Rot = Quaternion.Inverse(parent.rotation) * gadget.transform.rotation,
                Data = Portable(GadgetState.Data(gadget)).ToString(Formatting.None),
            };
            // Etiketten verschwinden beim Abbauen (DestroyOnGadgetUnlink) -- dann bekommt auch der Client nichts.
            if (gadget.GetComponent<DestroyOnGadgetUnlink>() != null) result.Prefab = string.Empty;
            GadgetItem item = gadget.Remove();
            if (item == null) return "Abbauen abgelehnt: das Spiel hat es verweigert.";
            // Der Gegenstand gehoert dem Client -- beim Host liegt keiner herum.
            if (item.Item != null) SingletonBehaviour<StorageController>.Instance.RemoveItemFromWorldStorage(item.Item);
            Object.Destroy(gadget.gameObject);
            Object.Destroy(item.gameObject);

            result.Op = GadgetBuildPacket.RESULT;
            result.Id = packet.Id;
            result.Ok = true;
            result.CarGuid = packet.CarGuid;
            MultiplayerAPI.Server.SendSerializablePacketToPlayer(result, sender);
            SendIfChanged(((TrainCarCustomization)custom).TrainCar);
            return null;
        }

        private static void Reply(GadgetBuildPacket request, IPlayer player, bool ok, string message, int uid = 0)
        {
            if (MultiplayerAPI.Server == null) return;
            if (!ok) Main.Log.Log(player.DisplayName + ": " + message);
            MultiplayerAPI.Server.SendSerializablePacketToPlayer(new GadgetBuildPacket
            {
                Op = GadgetBuildPacket.RESULT, Id = request.Id, Ok = ok, Message = message ?? string.Empty, CarGuid = request.CarGuid,
                Uid = uid,
            }, player);
        }

        /// <summary>
        /// Gadget-Zustand, der mit dem Gegenstand wandert. Ohne alles, was zur Montagestelle gehoert:
        /// Kennung, Kabel, Halterungsbezug, Bohrloecher, "auf Glas" -- das bestimmt der neue Ort.
        /// </summary>
        private static JObject Portable(JObject data)
        {
            JObject copy = (JObject)data.DeepClone();
            foreach (string key in new[] { "uid", "links", "mounted", "holes", "onGlass" }) copy.Remove(key);
            return copy;
        }
    }
}
