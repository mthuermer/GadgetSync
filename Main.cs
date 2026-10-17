using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DV;
using DV.CabControls;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Utils;
using MPAPI;
using MPAPI.Interfaces;
using MPAPI.Interfaces.Packets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityModManagerNet;

namespace GadgetMirror
{
    /// <summary>
    /// Spiegelt eingebaute Gadgets zwischen Host und Clients.
    ///
    /// Host -> Clients: der Host pollt seinen Zustand (Gadgets, Kabel, Stellung der Bedienelemente)
    /// und sendet pro Wagen bei Aenderung. Clients bauen daraus Spiegel-Gadgets.
    ///
    /// Clients -> Host: wer bedient, fuehrt aus. Ein Client betaetigt sein Spiegel-Gadget ganz
    /// normal (Weichen synchronisiert die MP-Mod selbst), und schickt danach, was sich daran geaendert hat,
    /// an den Host. Der Host uebernimmt es und verteilt seinen Zustand an alle. Der Host
    /// fuehrt die Aktion also nie ein zweites Mal aus -- und das funktioniert auch, wenn er
    /// weit weg ist und seine Gadget-Detailmodelle gar nicht geladen hat.
    ///
    /// Pakete der MP-API kommen zuverlaessig, aber nicht unbedingt in Reihenfolge an (bei Direktverbindung;
    /// Steam stellt geordnet zu). Deshalb tragen Host-Staende und Meldungen Laufnummern: Veraltetes wird
    /// verworfen, und der Host bestaetigt jede Meldung mit der Nummer des Host-Stands, der sie enthaelt.
    /// Bis dahin behaelt der Client seine Eingabe -- entschieden wird nach Nummern, nicht nach Zeit.
    ///
    /// Schrauben, Kleben, Loeten, Verkabeln durch Clients laufen wie Bedienen (lokal tun, Delta melden).
    /// Anbringen und Abbauen dagegen fuehrt der Host aus -- es braucht Gegenstaende. Beides: Build.cs.
    /// Bedienen und Bauen gehen nur zwischen gleichen Protokollversionen (GadgetHelloPacket).
    /// </summary>
    public static class Main
    {
        internal static UnityModManager.ModEntry.ModLogger Log;
        internal static string Version = "?";

        /// <summary>
        /// Aufbau der Pakete. Bedienungen gehen nur zwischen gleichen Protokollen hin und her.
        /// 3: Push als Delta (seit 0.2.2) plus Versionsabgleich (seit 0.2.3).
        /// 4: Delta enthaelt auch Kabel (seit 0.4.0, Verkabeln durch Clients).
        /// 5: Texte (Host laedt auch AfterSaveDataLoaded), Antwort aufs Anbringen traegt die UID (seit 0.5.0).
        /// 6: Laufnummern in Host-Stand und Meldung, Host bestaetigt Meldungen (seit 0.7.0).
        /// </summary>
        internal static int Protocol = 6;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Log = modEntry.Logger;
            Version = modEntry.Info.Version;
            GameObject go = new GameObject("GadgetMirror");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Mirror>();
            new HarmonyLib.Harmony(modEntry.Info.Id).PatchAll();   // Bauen durch Clients, siehe Build.cs
            return true;
        }
    }

    /// <summary>Host -> Clients: vollstaendiger Gadget-Zustand eines Wagens.</summary>
    public class GadgetStatePacket : ISerializablePacket
    {
        public string CarGuid = string.Empty;
        public int Seq;   // steigt mit jedem gesendeten Stand -- aeltere, die spaeter ankommen, sind ueberholt
        public string Payload = "[]";

        public void Serialize(BinaryWriter w)
        {
            w.Write(CarGuid ?? string.Empty);
            w.Write(Seq);
            w.Write(Payload ?? "[]");
        }

        public void Deserialize(BinaryReader r)
        {
            CarGuid = r.ReadString();
            Seq = r.ReadInt32();
            Payload = r.ReadString();
        }
    }

    /// <summary>
    /// Client -> Host: Aenderung an einem Gadget nach Bedienung (Delta, siehe GadgetState.Delta), mit Laufnummer.
    /// Host -> Client: Bestaetigung dieser Laufnummer (State leer); ab Host-Stand Snap ist die Aenderung enthalten.
    /// </summary>
    public class GadgetPushPacket : ISerializablePacket
    {
        public string CarGuid = string.Empty;
        public int Uid;
        public int Seq;
        public int Snap;
        public string State = "{}";

        public void Serialize(BinaryWriter w)
        {
            w.Write(CarGuid ?? string.Empty);
            w.Write(Uid);
            w.Write(Seq);
            w.Write(Snap);
            w.Write(State ?? "{}");
        }

        public void Deserialize(BinaryReader r)
        {
            CarGuid = r.ReadString();
            Uid = r.ReadInt32();
            Seq = r.ReadInt32();
            Snap = r.ReadInt32();
            State = r.ReadString();
        }
    }

    /// <summary>
    /// Host -> Client, sobald der Spieler bereit ist; der Client antwortet mit seiner eigenen.
    /// Ohne passende Antwort uebernimmt der Host keine Bedienung dieses Clients: ein anders
    /// aufgebautes Push-Paket wuerde Gadgets beim Host falsch setzen.
    /// </summary>
    public class GadgetHelloPacket : ISerializablePacket
    {
        public int Protocol;
        public string Version = string.Empty;

        public void Serialize(BinaryWriter w)
        {
            w.Write(Protocol);
            w.Write(Version ?? string.Empty);
        }

        public void Deserialize(BinaryReader r)
        {
            Protocol = r.ReadInt32();
            Version = r.ReadString();
        }

        internal static GadgetHelloPacket Mine()
        {
            return new GadgetHelloPacket { Protocol = Main.Protocol, Version = Main.Version };
        }
    }

    /// <summary>Zustand eines Gadgets lesen und schreiben -- auf Host und Client gleich.</summary>
    internal static class GadgetState
    {
        /// <summary>Solange &gt; 0, stammen Aenderungen an Bedienelementen von uns, nicht vom Spieler.</summary>
        internal static int Applying;

        /// <summary>
        /// Bedienelemente mit dauerhaftem Wert (Hebel, Drehknoepfe, rastende Taster) in fester
        /// Reihenfolge. Momenttaster sind nur kurz gedrueckt -- ihr Wert wird nicht uebertragen,
        /// ihre Wirkung landet im Gadget-Zustand. Rastende Taster (Spec.Button.isToggle) dagegen
        /// merken sich an/aus in ihrem eigenen Value: ohne Uebertragung schaltet der naechste
        /// Druck auf der anderen Seite falsch herum.
        /// </summary>
        internal static List<ControlImplBase> ValueControls(GadgetBase gadget)
        {
            List<ControlImplBase> list = new List<ControlImplBase>();
            foreach (ControlImplBase c in gadget.GetComponentsInChildren<ControlImplBase>(true))
                if (!(c is ButtonBase) || IsToggle(c)) list.Add(c);
            return list;
        }

        internal static bool IsToggle(ControlImplBase control)
        {
            DV.CabControls.Spec.Button spec = control.GetComponent<DV.CabControls.Spec.Button>();
            return spec != null && spec.isToggle;
        }

        internal static JObject WithoutLinks(JObject data)
        {
            if (data == null) return new JObject();
            if (data["links"] == null) return data;
            JObject copy = (JObject)data.DeepClone();
            copy.Remove("links");
            return copy;
        }

        internal static JObject Data(GadgetBase gadget)
        {
            JObject data = new JObject();
            try
            {
                gadget.SaveDataRequested(data);
            }
            catch (Exception ex)
            {
                Main.Log.Warning("SaveDataRequested " + gadget.GetType().Name + ": " + ex.Message);
            }
            JArray links = data["links"] as JArray;   // Reihenfolge ist Zufall -- sortiert vergleichbar
            if (links != null) data["links"] = new JArray(links.Select(t => (int)t).OrderBy(uid => uid));
            return data;
        }

        /// <summary>Stellungen; gleitet ein Bedienelement gerade, zaehlt sein Ziel -- nicht die Zwischenstellung.</summary>
        internal static JArray Controls(GadgetBase gadget)
        {
            JArray arr = new JArray();
            Glide glide;
            foreach (ControlImplBase c in ValueControls(gadget)) arr.Add(glides.TryGetValue(c, out glide) ? glide.To : c.Value);
            return arr;
        }

        private const float GLIDE_SECONDS = 0.12f;   // etwas laenger als der Sendetakt -- durchgehende Bewegung

        private class Glide
        {
            public float From, To, Start;
        }

        private static readonly Dictionary<ControlImplBase, Glide> glides = new Dictionary<ControlImplBase, Glide>();

        /// <summary>Laufende Gleitbewegungen einen Schritt weiter (jedes Bild).</summary>
        internal static void TickGlides()
        {
            if (glides.Count == 0) return;
            List<ControlImplBase> done = new List<ControlImplBase>();
            Applying++;
            try
            {
                foreach (KeyValuePair<ControlImplBase, Glide> entry in glides)
                {
                    ControlImplBase c = entry.Key;
                    float t = (Time.time - entry.Value.Start) / GLIDE_SECONDS;
                    if (c == null || IsHeld(c)) { done.Add(c); continue; }
                    SetSafely(c, Mathf.Lerp(entry.Value.From, entry.Value.To, Mathf.Clamp01(t)));
                    if (t >= 1f) done.Add(c);
                }
            }
            finally
            {
                Applying--;
            }
            foreach (ControlImplBase c in done) glides.Remove(c);
        }

        private static bool IsHeld(ControlImplBase c)
        {
            try
            {
                return c.IsGrabbed();
            }
            catch
            {
                return false;   // Bedienelement im inaktiven Detailmodell
            }
        }

        private static void SetSafely(ControlImplBase c, float v)
        {
            try
            {
                // Setzt den Wert, loest die Gadget-Reaktion aus und bewegt das Bedienelement.
                // Liegt es in einem inaktiven Detailmodell, kann nur das Bewegen scheitern.
                c.SetValue(v);
            }
            catch (Exception ex)
            {
                Main.Log.Warning("SetValue " + c.name + ": " + ex.Message);
            }
        }

        /// <param name="smooth">Stufenlose Bedienelemente (ohne Raste) gleiten zum Wert statt zu springen.</param>
        internal static void ApplyControls(GadgetBase gadget, JArray values, bool smooth = false)
        {
            if (values == null) return;
            List<ControlImplBase> controls = ValueControls(gadget);
            int n = Math.Min(controls.Count, values.Count);
            Applying++;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    ControlImplBase c = controls[i];
                    if (values[i].Type == JTokenType.Null) continue;   // Delta: diese Stellung unveraendert
                    float v = (float)values[i];
                    if (c == null || IsHeld(c)) continue;   // der Spieler haelt es gerade -- nicht aus der Hand reissen
                    Glide glide;
                    if (glides.TryGetValue(c, out glide) && glide.To == v) continue;
                    if (c.Value == v) { glides.Remove(c); continue; }
                    if (smooth && !(c is ButtonBase) && c.GetComponent<SteppedJoint>() == null)
                        glides[c] = new Glide { From = c.Value, To = v, Start = Time.time };
                    else
                    {
                        glides.Remove(c);
                        SetSafely(c, v);
                    }
                }
            }
            finally
            {
                Applying--;
            }
        }

        /// <summary>
        /// Was der Client gegenueber der Basis geaendert hat:
        /// {"d": nur geaenderte Felder (auch Kabel), "c": geaenderte Stellungen, sonst null}. null = nichts.
        /// Nur das Delta geht an den Host -- unveraenderte Felder waeren evtl. schon veraltet
        /// (der Host hat inzwischen geloetet, verschraubt, ...) und wuerden ihn zuruecksetzen.
        /// </summary>
        /// <param name="force">Felder und Stellungen eines frueheren Deltas, die mit ihrem jetzigen Wert mitgehen</param>
        internal static JObject Delta(JObject data, JArray controls, JObject host, JObject force = null)
        {
            JObject hostData = (host != null ? host["d"] as JObject : null) ?? new JObject();
            JArray hostControls = host != null ? host["c"] as JArray : null;
            JObject forceData = force != null ? force["d"] as JObject : null;
            JArray forceControls = force != null ? force["c"] as JArray : null;
            JObject d = new JObject();
            foreach (JProperty prop in data.Properties())
                if (!Same(prop.Value, hostData[prop.Name]) || (forceData != null && forceData[prop.Name] != null)) d[prop.Name] = prop.Value;
            JArray c = new JArray();
            bool any = d.Count > 0;
            for (int i = 0; i < controls.Count; i++)
            {
                bool same = hostControls != null && i < hostControls.Count && Same(controls[i], hostControls[i]) &&
                            !(forceControls != null && i < forceControls.Count && forceControls[i].Type != JTokenType.Null);
                c.Add(same ? JValue.CreateNull() : controls[i]);
                any |= !same;
            }
            if (!any) return null;
            JObject delta = new JObject();
            delta["d"] = d;
            delta["c"] = c;
            return delta;
        }

        private const double SAME_TOLERANCE = 0.02;

        /// <summary>
        /// Gleich, Zahlen bis auf 2 %. Ein Drehknopf liest nach jedem uebernommenen Wert seine physische
        /// Stellung zurueck (0,098 statt 0,1) -- das ist keine Eingabe und darf nichts an den Host schicken.
        /// </summary>
        private static bool Same(JToken a, JToken b)
        {
            bool numbers = a != null && b != null &&
                           (a.Type == JTokenType.Float || a.Type == JTokenType.Integer) &&
                           (b.Type == JTokenType.Float || b.Type == JTokenType.Integer);
            return numbers ? Math.Abs((double)a - (double)b) <= SAME_TOLERANCE : JToken.DeepEquals(a, b);
        }

        /// <summary>
        /// Felder in den aktuellen Zustand einmischen und laden. Nie Teil-Daten direkt laden: fehlende
        /// Felder setzt das Spiel sonst zurueck (Kennung auf 0, Bohrloecher "missing").
        /// </summary>
        internal static void Merge(GadgetBase gadget, JObject changes)
        {
            JObject data = WithoutLinks(Data(gadget));
            foreach (JProperty prop in changes.Properties())
                if (prop.Name != "links") data[prop.Name] = prop.Value;
            Load(gadget, data, false);
            // Manches laedt das Spiel erst danach (Text). Halterungen nicht: die setzten ihr Gadget neu auf.
            if (gadget.GetComponent<Mount>() == null) Load(gadget, data, true);
        }

        internal static void Load(GadgetBase gadget, JObject data, bool after)
        {
            if (gadget == null) return;
            try
            {
                if (after) gadget.AfterSaveDataLoaded(data);
                else gadget.SaveDataLoaded(data);
            }
            catch (Exception ex)
            {
                Main.Log.Warning((after ? "AfterSaveDataLoaded " : "SaveDataLoaded ") + gadget.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Kabel eines Gadgets an die gewuenschte Liste von Gegenstuecken (UIDs) angleichen.
        /// Nur Unterschiede: bestehende Kabel bleiben, fehlende werden verlegt, ueberzaehlige
        /// abgezogen. Laeuft erst, wenn alle Gadgets des Wagens ihre Host-UID tragen.
        /// </summary>
        internal static void ReconcileLinks(Customization custom, GadgetBase gadget, JObject data, ICollection<GadgetBase> keepLocal = null)
        {
            if (!gadget.HasAnyWirePorts || (keepLocal != null && keepLocal.Contains(gadget))) return;
            HashSet<int> desired = new HashSet<int>();
            JArray links = data != null ? data["links"] as JArray : null;
            if (links != null)
                foreach (JToken t in links) desired.Add((int)t);

            HashSet<int> present = new HashSet<int>();
            List<GadgetWiringModule.WireLinkPort> buffer = new List<GadgetWiringModule.WireLinkPort>();
            foreach (GadgetWiringModule.WireLinkPort mine in gadget.WireLinkPorts)
            {
                buffer.Clear();
                mine.GetLinks(buffer);
                foreach (GadgetWiringModule.WireLinkPort other in buffer)
                {
                    int uid = other.owner != null ? other.owner.UID : 0;
                    if (desired.Contains(uid)) present.Add(uid);
                    else if (keepLocal == null || !keepLocal.Contains(other.owner)) GadgetWiringModule.WireLinkPort.Unwire(mine, other);
                }
            }

            foreach (int uid in desired)
            {
                if (present.Contains(uid)) continue;
                TrainCarCustomization.TrainCarCustomizerBase customizer;
                GadgetBase other = custom.TryGetCustomizerByUID(uid, out customizer) ? customizer as GadgetBase : null;
                GadgetWiringModule.WireLinkPort myPort, otherPort;
                if (other == null || (keepLocal != null && keepLocal.Contains(other))) continue;   // dessen Kabel gelten lokal
                if (!gadget.TryGetCompatiblePorts(other, out myPort, out otherPort)) continue;
                if (!GadgetWiringModule.WireLinkPort.AreWired(myPort, otherPort))
                    GadgetWiringModule.WireLinkPort.Wire(myPort, otherPort);
            }
        }
    }

    public partial class Mirror : MonoBehaviour
    {
        private const float HOST_POLL_SECONDS = 1f;          // alles abgleichen -- faengt auch Gadget-eigene Logik
        private const float HOST_FAST_SECONDS = 0.1f;        // vom Spieler beim Host Geaendertes: sofort, hoechstens so oft
        private const float CLIENT_TICK_SECONDS = 0.05f;
        private const float PUSH_INTERVAL_SECONDS = 0.1f;    // Bedienung beim Client: hoechstens so oft senden, auch bei Dauerbetaetigung
        private const float PENDING_TIMEOUT_SECONDS = 3f;    // nur Notfall: so lange wartet eine Meldung auf die Bestaetigung

        // Host: CarGUID -> zuletzt gesendetes Payload bzw. dessen Laufnummer
        private readonly Dictionary<string, string> lastSent = new Dictionary<string, string>();
        private readonly Dictionary<string, int> lastSentSeq = new Dictionary<string, int>();
        private int snapSeq;
        // Host: "Spieler|Wagen|UID" -> Laufnummer der zuletzt uebernommenen Meldung
        private readonly Dictionary<string, int> lastPush = new Dictionary<string, int>();

        // Client: CarGUID -> gewuenschtes bzw. bereits angewandtes Payload, und Laufnummer des gewuenschten
        private readonly Dictionary<string, string> wanted = new Dictionary<string, string>();
        private readonly Dictionary<string, int> wantedSeq = new Dictionary<string, int>();
        private readonly Dictionary<string, string> applied = new Dictionary<string, string>();
        // Client: CarGUID -> von uns erzeugte Item-GameObjects
        private readonly Dictionary<string, List<GameObject>> mirrored = new Dictionary<string, List<GameObject>>();
        // Client: vom Spieler bediente Spiegel-Gadgets -> (Wagen, Zeitpunkt), noch nicht gesendet
        private readonly Dictionary<GadgetBase, KeyValuePair<string, float>> dirty = new Dictionary<GadgetBase, KeyValuePair<string, float>>();
        // Client: zuletzt angewandter Host-Stand je Spiegel-Gadget -- Basis fuer das Delta
        private readonly Dictionary<GadgetBase, JObject> hostState = new Dictionary<GadgetBase, JObject>();

        /// <summary>Client: gesendete Bedienung eines Spiegel-Gadgets, bis ein Host-Stand sie enthaelt.</summary>
        private class Inflight
        {
            public JObject Delta;       // alles seit der letzten Bestaetigung Gesendete
            public JObject SentState;   // eigener Stand beim letzten Senden -- Basis fuer das naechste Delta
            public int Sent, Acked;     // Laufnummer der letzten Meldung bzw. der letzten Bestaetigung
            public int Snap;            // ab diesem Host-Stand ist die bestaetigte Meldung enthalten
            public float Time;          // letztes Senden
        }

        private readonly Dictionary<GadgetBase, Inflight> inflight = new Dictionary<GadgetBase, Inflight>();
        private int pushSeq;
        // Client: Wagen, bei denen ein Host-Stand wegen eigener Bedienung ausgelassen wurde --
        // wird nachgeholt, sobald die Bedienung bestaetigt oder abgelaufen ist
        private readonly HashSet<string> retry = new HashSet<string>();

        private const float HELLO_TIMEOUT_SECONDS = 15f;      // so lange darf die Versionsantwort dauern

        // Host: Spieler mit passendem Protokoll bzw. bereits gemeldete Abweichungen
        private readonly HashSet<byte> compatible = new HashSet<byte>();
        private readonly HashSet<byte> warned = new HashSet<byte>();
        // Client: der Host spricht unser Protokoll -- vorher wird nichts gesendet
        private bool hostCompatible;

        // Client: Spiegel-Item -> Eintrag (Art + Lage), aus dem es gebaut wurde -- zum Wiederverwenden
        private readonly Dictionary<GameObject, string> builtFrom = new Dictionary<GameObject, string>();

        private Dictionary<string, GameObject> itemPrefabs;
        private bool clientBusy;

        private void Awake()
        {
            Instance = this;
            MultiplayerAPI.ServerStarted += OnServerStarted;
            MultiplayerAPI.ClientStarted += OnClientStarted;
            MultiplayerAPI.ServerStopped += OnServerStopped;
            MultiplayerAPI.ClientStopped += OnClientStopped;
        }

        private void OnServerStarted(IServer server)
        {
            lastSent.Clear();
            lastPush.Clear();
            // Neuer Spieler: alles vergessen, damit der naechste Durchlauf alles neu sendet.
            compatible.Clear();
            warned.Clear();
            server.OnPlayerReady += OnPlayerReady;
            server.OnPlayerDisconnected += p => { compatible.Remove(p.PlayerId); warned.Remove(p.PlayerId); ForgetPushes(p.PlayerId); };
            server.RegisterSerializablePacket<GadgetPushPacket>(OnPushPacket);
            server.RegisterSerializablePacket<GadgetHelloPacket>(OnHelloFromClient);
            server.RegisterSerializablePacket<GadgetBuildPacket>(OnBuildRequest);
            StopCoroutine("HostLoop");
            StartCoroutine("HostLoop");
        }

        private void OnPlayerReady(IPlayer player)
        {
            lastSent.Clear();
            ForgetPushes(player.PlayerId);   // neue Sitzung des Clients zaehlt wieder von vorn
            if (player.IsHost || MultiplayerAPI.Server == null) return;
            MultiplayerAPI.Server.SendSerializablePacketToPlayer(GadgetHelloPacket.Mine(), player);
            StartCoroutine(ExpectHello(player));
        }

        private void OnHelloFromClient(GadgetHelloPacket packet, IPlayer sender)
        {
            if (packet.Protocol == Main.Protocol) compatible.Add(sender.PlayerId);
            else WarnMismatch(sender, "GadgetMirror " + packet.Version);
        }

        /// <summary>Clients vor 0.2.3 kennen den Versionsabgleich nicht und antworten nie.</summary>
        private IEnumerator ExpectHello(IPlayer player)
        {
            yield return new WaitForSeconds(HELLO_TIMEOUT_SECONDS);
            IServer server = MultiplayerAPI.Server;
            if (server == null || compatible.Contains(player.PlayerId)) yield break;
            bool connected = false;
            foreach (IPlayer p in server.Players) connected |= p.PlayerId == player.PlayerId;
            if (!connected) yield break;
            WarnMismatch(player, "eine GadgetMirror-Version vor 0.2.3");
        }

        private void WarnMismatch(IPlayer player, string theirs)
        {
            if (!warned.Add(player.PlayerId)) return;
            string text = "GadgetMirror: " + player.DisplayName + " hat " + theirs + ", der Host " + Main.Version +
                          ". " + player.DisplayName + " sieht die Gadgets, kann sie aber nicht bedienen -- bitte dieselbe Version installieren.";
            Main.Log.Warning(text);
            if (MultiplayerAPI.Server != null) MultiplayerAPI.Server.SendServerChatMessage(text);
        }

        private void OnServerStopped()
        {
            StopCoroutine("HostLoop");
            lastSent.Clear();
        }

        private void OnClientStarted(IClient client)
        {
            ClearAllMirrors();
            hostCompatible = false;
            client.RegisterSerializablePacket<GadgetStatePacket>(OnStatePacket);
            client.RegisterSerializablePacket<GadgetHelloPacket>(OnHelloFromHost);
            client.RegisterSerializablePacket<GadgetBuildPacket>(OnBuildResult);
            client.RegisterSerializablePacket<GadgetPushPacket>(OnPushAck);
            StopCoroutine("ExpectHostHello");
            StartCoroutine("ExpectHostHello");
            StopCoroutine("ClientLoop");
            StartCoroutine("ClientLoop");
        }

        private void OnClientStopped()
        {
            StopCoroutine("ClientLoop");
            StopCoroutine("ExpectHostHello");
            hostCompatible = false;
            ClearAllMirrors();
        }

        // ---------------------------------------------------------------- Host

        private IEnumerator HostLoop()
        {
            WaitForSeconds wait = new WaitForSeconds(HOST_POLL_SECONDS);
            List<string> gone = new List<string>();
            HashSet<string> seen = new HashSet<string>();

            while (true)
            {
                yield return wait;
                if (MultiplayerAPI.Server == null || !WorldStreamingInit.IsLoaded) continue;

                CarSpawner spawner = SingletonBehaviour<CarSpawner>.Instance;
                if (spawner == null) continue;

                seen.Clear();
                List<TrainCar> cars = spawner.AllCars;
                for (int i = 0; i < cars.Count; i++)
                {
                    TrainCar car = cars[i];
                    if (SendIfChanged(car)) seen.Add(car.CarGUID);
                }

                // Despawnte Wagen vergessen, damit sie beim naechsten Spawn neu gemeldet werden.
                gone.Clear();
                foreach (KeyValuePair<string, string> entry in lastSent)
                    if (!seen.Contains(entry.Key)) gone.Add(entry.Key);
                for (int i = 0; i < gone.Count; i++) lastSent.Remove(gone[i]);
            }
        }

        /// <summary>Zustand des Wagens erfassen und senden, falls geaendert. true = Wagen ist relevant.</summary>
        private bool SendIfChanged(TrainCar car)
        {
            if (car == null || car.Customization == null || string.IsNullOrEmpty(car.CarGUID)) return false;

            string payload = Capture(car);
            // Wagen ohne Gadgets interessieren nur, wenn wir vorher welche gemeldet hatten.
            if (payload == "[]" && !lastSent.ContainsKey(car.CarGUID)) return false;

            string previous;
            if (lastSent.TryGetValue(car.CarGUID, out previous) && previous == payload) return true;

            lastSent[car.CarGUID] = payload;
            lastSentSeq[car.CarGUID] = ++snapSeq;
            MultiplayerAPI.Server.SendSerializablePacketToAll(
                new GadgetStatePacket { CarGuid = car.CarGUID, Seq = snapSeq, Payload = payload }, true, true);
            return true;
        }

        private string Capture(TrainCar car)
        {
            JArray arr = new JArray();
            IList<Customization.CustomizerBase> customizers = car.Customization.Customizers;

            for (int i = 0; i < customizers.Count; i++)
            {
                GadgetBase gadget = customizers[i] as GadgetBase;
                if (gadget == null || gadget.GadgetItem == null) continue;

                InventoryItemSpec spec = gadget.GadgetItem.GetComponent<InventoryItemSpec>();
                if (spec == null || string.IsNullOrEmpty(spec.ItemPrefabName)) continue;

                Vector3 p = gadget.transform.localPosition;
                Vector3 r = gadget.transform.localEulerAngles;

                JObject entry = new JObject();
                entry["p"] = spec.ItemPrefabName;
                entry["x"] = p.x;
                entry["y"] = p.y;
                entry["z"] = p.z;
                entry["rx"] = r.x;
                entry["ry"] = r.y;
                entry["rz"] = r.z;
                entry["d"] = GadgetState.Data(gadget);          // inklusive Kabel ("links")
                entry["c"] = GadgetState.Controls(gadget);      // Stellung der Bedienelemente
                arr.Add(entry);
            }

            return arr.ToString(Formatting.None);
        }

        /// <summary>
        /// Ein Client hat ein Spiegel-Gadget bedient: die geaenderten Felder ins echte Gadget
        /// uebernehmen (Kabel per Abgleich), sofort an alle verteilen und dem Client bestaetigen.
        /// </summary>
        private void OnPushPacket(GadgetPushPacket packet, IPlayer sender)
        {
            if (packet == null || string.IsNullOrEmpty(packet.CarGuid) || packet.Uid == 0) return;
            if (!compatible.Contains(sender.PlayerId)) return;   // andere Version: siehe WarnMismatch

            // Jede Meldung enthaelt alles noch Unbestaetigte: eine aeltere, die erst danach ankommt, ist ueberholt.
            string key = sender.PlayerId + "|" + packet.CarGuid + "|" + packet.Uid;
            int last;
            if (!lastPush.TryGetValue(key, out last) || packet.Seq > last)
            {
                lastPush[key] = packet.Seq;
                ApplyPush(packet);
            }

            // Immer bestaetigen, auch Verworfenes -- sonst haelt der Client seine Eingabe bis zum Notfall-Timeout.
            int snap;
            lastSentSeq.TryGetValue(packet.CarGuid, out snap);
            MultiplayerAPI.Server.SendSerializablePacketToPlayer(
                new GadgetPushPacket { CarGuid = packet.CarGuid, Uid = packet.Uid, Seq = packet.Seq, Snap = snap, State = "" }, sender);
        }

        private void ForgetPushes(byte playerId)
        {
            string prefix = playerId + "|";
            foreach (string key in lastPush.Keys.Where(k => k.StartsWith(prefix)).ToList()) lastPush.Remove(key);
        }

        private void ApplyPush(GadgetPushPacket packet)
        {
            Customization custom;
            if (!Customization.TryGetFromIdentificationKey(packet.CarGuid, out custom) || custom == null) return;

            TrainCarCustomization.TrainCarCustomizerBase customizer;
            GadgetBase gadget = custom.TryGetCustomizerByUID(packet.Uid, out customizer) ? customizer as GadgetBase : null;
            if (gadget == null)
            {
                Main.Log.Warning("Bedienung fuer unbekanntes Gadget uid" + packet.Uid + " verworfen");
                return;
            }

            JObject state;
            try
            {
                state = JObject.Parse(packet.State);
            }
            catch (Exception ex)
            {
                Main.Log.Warning("Bedienung unlesbar: " + ex.Message);
                return;
            }

            // Nur die geaenderten Felder kommen an: in den aktuellen Zustand einmischen.
            JObject delta = state["d"] as JObject;
            if (delta != null && delta.Count > 0)
            {
                GadgetState.Merge(gadget, delta);
                if (delta["links"] != null) GadgetState.ReconcileLinks(custom, gadget, delta);   // verkabelt/abgezogen
            }
            GadgetState.ApplyControls(gadget, state["c"] as JArray, smooth: true);

            TrainCarCustomization trainCustom = custom as TrainCarCustomization;
            if (trainCustom != null) SendIfChanged(trainCustom.TrainCar);
        }

        // -------------------------------------------------------------- Client

        private void OnHelloFromHost(GadgetHelloPacket packet)
        {
            if (MultiplayerAPI.Instance != null && MultiplayerAPI.Instance.IsHost) return;
            MultiplayerAPI.Client.SendSerializablePacketToServer(GadgetHelloPacket.Mine());
            hostCompatible = packet.Protocol == Main.Protocol;   // Abweichung meldet der Host im Chat
        }

        /// <summary>Ein Host vor 0.2.3 meldet sich nie -- dann bleibt Bedienen gesperrt, und wir sagen es.</summary>
        private IEnumerator ExpectHostHello()
        {
            while (!WorldStreamingInit.IsLoaded) yield return null;
            yield return new WaitForSeconds(HELLO_TIMEOUT_SECONDS);
            if (hostCompatible || (MultiplayerAPI.Instance != null && MultiplayerAPI.Instance.IsHost)) yield break;
            ChatLocal("GadgetMirror: Der Host hat eine GadgetMirror-Version vor 0.2.3, du " + Main.Version +
                      ". Du siehst die Gadgets, kannst sie aber nicht bedienen -- bitte dieselbe Version installieren.");
        }

        /// <summary>Meldung nur im eigenen Chat der MP-Mod (keine API dafuer -- per Reflection, sonst nur Log).</summary>
        private static void ChatLocal(string text)
        {
            Main.Log.Warning(text);
            try
            {
                Type type = Type.GetType("Multiplayer.Components.Networking.UI.ChatGUI, Multiplayer");
                UnityEngine.Object chat = type != null ? UnityEngine.Object.FindObjectOfType(type) : null;
                if (chat != null) type.GetMethod("ReceiveMessage").Invoke(chat, new object[] { text });
            }
            catch (Exception)
            {
                // ohne Chat bleibt die Warnung im Log
            }
        }

        private void OnStatePacket(GadgetStatePacket packet)
        {
            if (packet == null || string.IsNullOrEmpty(packet.CarGuid)) return;
            if (MultiplayerAPI.Instance != null && MultiplayerAPI.Instance.IsHost) return;
            int seen;
            if (wantedSeq.TryGetValue(packet.CarGuid, out seen) && packet.Seq <= seen) return;   // ueberholt
            wantedSeq[packet.CarGuid] = packet.Seq;
            wanted[packet.CarGuid] = packet.Payload ?? "[]";
        }

        /// <summary>Der Host hat eine Meldung uebernommen (oder verworfen); ab Host-Stand Snap ist sie enthalten.</summary>
        private void OnPushAck(GadgetPushPacket packet)
        {
            Customization custom;
            TrainCarCustomization.TrainCarCustomizerBase found;
            GadgetBase gadget = Customization.TryGetFromIdentificationKey(packet.CarGuid, out custom) && custom != null &&
                                custom.TryGetCustomizerByUID(packet.Uid, out found) ? found as GadgetBase : null;
            Inflight f;
            if (gadget == null || !inflight.TryGetValue(gadget, out f) || packet.Seq <= f.Acked) return;
            f.Acked = packet.Seq;
            f.Snap = packet.Snap;
            retry.Add(packet.CarGuid);   // ausgelassenen Host-Stand nachholen
        }

        private IEnumerator ClientLoop()
        {
            WaitForSeconds wait = new WaitForSeconds(CLIENT_TICK_SECONDS);
            List<string> keys = new List<string>();

            while (true)
            {
                yield return wait;
                if (clientBusy || !WorldStreamingInit.IsLoaded) continue;

                ExpireBuildRequests();
                DropBrokenMirrors();

                keys.Clear();
                keys.AddRange(wanted.Keys);

                for (int i = 0; i < keys.Count; i++)
                {
                    string guid = keys[i];
                    string target;
                    if (!wanted.TryGetValue(guid, out target)) continue;
                    int seq;
                    wantedSeq.TryGetValue(guid, out seq);

                    string current;
                    applied.TryGetValue(guid, out current);
                    if (current == target && (!retry.Contains(guid) || AnyUnresolved(guid))) continue;

                    Customization custom;
                    // Wagen (noch) nicht gespawnt: beim naechsten Durchlauf erneut versuchen.
                    if (!Customization.TryGetFromIdentificationKey(guid, out custom) || custom == null) continue;

                    clientBusy = true;
                    yield return Rebuild(guid, custom, target, current, seq);
                    clientBusy = false;
                }
            }
        }

        /// <summary>Vom Spieler bediente Spiegel-Gadgets: Aenderung an den Host schicken.</summary>
        private void SendPushes()
        {
            if (dirty.Count == 0 || MultiplayerAPI.Client == null) return;
            if (!hostCompatible)
            {
                // Host versteht uns nicht: nichts senden, Eingabe auf den Host-Stand zuruecksetzen.
                foreach (KeyValuePair<GadgetBase, KeyValuePair<string, float>> entry in dirty) retry.Add(entry.Value.Key);
                dirty.Clear();
                return;
            }
            // Erste Aenderung sofort, danach hoechstens alle PUSH_INTERVAL_SECONDS je Gadget.
            List<GadgetBase> ready = new List<GadgetBase>();
            foreach (KeyValuePair<GadgetBase, KeyValuePair<string, float>> entry in dirty)
            {
                Inflight sent;
                if (!inflight.TryGetValue(entry.Key, out sent) || Time.time - sent.Time >= PUSH_INTERVAL_SECONDS) ready.Add(entry.Key);
            }

            foreach (GadgetBase gadget in ready)
            {
                string guid = dirty[gadget].Key;
                dirty.Remove(gadget);
                if (gadget == null || !gadget.IsLinked) continue;

                // Basis: unser Stand beim letzten Senden, solange noch etwas unterwegs ist, sonst der Host-Stand --
                // so gehen nur eigene Aenderungen raus, nie ein Host-Stand, den wir noch nicht uebernommen haben.
                // Dazu alles seit der letzten Bestaetigung schon Gesendete: Jede Meldung ist damit vollstaendig, und
                // "an und gleich wieder aus" geht nicht verloren ("aus" gleicht der Basis, geht aber trotzdem mit).
                // Ereignisse ohne echte Aenderung (Drehknopf liest seine Stellung zurueck) bleiben hier haengen.
                Inflight f;
                JObject basis;
                if (inflight.TryGetValue(gadget, out f)) basis = f.SentState;
                else hostState.TryGetValue(gadget, out basis);
                JObject data = GadgetState.Data(gadget);
                JArray controls = GadgetState.Controls(gadget);
                JObject delta = GadgetState.Delta(data, controls, basis, f != null && f.Acked < f.Sent ? f.Delta : null);
                if (delta == null) continue;
                if (f == null) inflight[gadget] = f = new Inflight();
                f.Delta = delta;
                f.SentState = new JObject { ["d"] = data, ["c"] = controls };
                f.Sent = ++pushSeq;
                f.Time = Time.time;
                MultiplayerAPI.Client.SendSerializablePacketToServer(
                    new GadgetPushPacket { CarGuid = guid, Uid = gadget.UID, Seq = f.Sent, State = delta.ToString(Formatting.None) }, true);
            }
        }

        /// <summary>Gedrosselt, nicht entprellt: der Zeitpunkt der ersten Aenderung zaehlt, nicht der letzten --
        /// sonst ginge bei Dauerbetaetigung (schnell schalten, Blende schieben) erst der Endstand raus.</summary>
        private void MarkDirty(GadgetBase gadget, string guid)
        {
            if (GadgetState.Applying > 0 || gadget == null || dirty.ContainsKey(gadget)) return;
            dirty[gadget] = new KeyValuePair<string, float>(guid, Time.time);
        }

        // Host: Wagen, an denen der Spieler beim Host etwas veraendert hat -- gehen sofort raus
        private readonly HashSet<TrainCar> hostDirty = new HashSet<TrainCar>();
        private float lastFastSend;
        // Host: Bedienelement -> Gadget (null = gehoert zu keinem, z. B. Lok-Hebel)
        private readonly Dictionary<ControlImplBase, GadgetBase> controlOwner = new Dictionary<ControlImplBase, GadgetBase>();

        internal void HostChanged(GadgetBase gadget)
        {
            if (MultiplayerAPI.Server == null || gadget == null) return;
            TrainCar car = gadget.TrainCar;
            if (car != null) hostDirty.Add(car);
        }

        /// <summary>Ein Bedienelement hat sich bewegt. Beim Host: gehoert es zu einem Gadget, sofort senden.</summary>
        internal void ControlChanged(ControlImplBase control)
        {
            if (MultiplayerAPI.Server == null || control == null) return;
            GadgetBase gadget;
            if (!controlOwner.TryGetValue(control, out gadget))
                controlOwner[control] = gadget = control.GetComponentInParent<GadgetBase>();
            if (gadget != null) HostChanged(gadget);
        }

        private void Update()
        {
            GadgetState.TickGlides();
            if (MultiplayerAPI.Client != null && dirty.Count > 0) SendPushes();
            if (MultiplayerAPI.Server != null && hostDirty.Count > 0 && Time.time - lastFastSend >= HOST_FAST_SECONDS)
            {
                lastFastSend = Time.time;
                foreach (TrainCar car in hostDirty) SendIfChanged(car);
                hostDirty.Clear();
            }
        }

        /// <summary>Bedienelemente eines Spiegel-Gadgets beobachten: Spieler-Eingaben gehen an den Host.</summary>
        private void HookControls(string guid, GadgetBase gadget)
        {
            foreach (ControlImplBase control in gadget.GetComponentsInChildren<ControlImplBase>(true))
            {
                control.ValueChanged += e => MarkDirty(gadget, guid);
                control.Used += () => MarkDirty(gadget, guid);
                control.UnUsed += () => MarkDirty(gadget, guid);   // Taster losgelassen
            }
        }

        /// <summary>
        /// Hat es einem Spiegel-Gadget lokal die Verbindung gerissen (Kollision, jemand hat
        /// es abgebaut), vergessen wir den Zustand -- dann baut die Schleife ihn neu auf.
        /// Ist der Wagen selbst weg (geloescht, despawnt), raeumen wir unsere Items ab: die
        /// Gadgets sterben mit dem Wagen, die Items liegen lose in der Szene und bleiben sonst
        /// fuer immer. 'wanted' bleibt stehen -- taucht der Wagen wieder auf, wird neu gebaut,
        /// denn der Host sendet einen unveraenderten Zustand nicht noch einmal.
        /// </summary>
        private void DropBrokenMirrors()
        {
            List<string> keys = new List<string>(mirrored.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                Customization custom;
                if (!Customization.TryGetFromIdentificationKey(keys[i], out custom) || custom == null)
                {
                    ClearMirror(keys[i]);
                    continue;
                }

                List<GameObject> spawned = mirrored[keys[i]];
                bool broken = false;
                for (int j = 0; j < spawned.Count && !broken; j++)
                {
                    if (spawned[j] == null) { broken = true; break; }
                    GadgetItem item = spawned[j].GetComponent<GadgetItem>();
                    if (item == null || item.Gadget == null || !item.Gadget.IsLinked) broken = true;
                }
                if (broken) applied.Remove(keys[i]);
            }
        }

        private IEnumerator Rebuild(string guid, Customization custom, string target, string current, int seq)
        {
            JArray entries = null;
            try
            {
                entries = JArray.Parse(target);
            }
            catch (Exception ex)
            {
                Main.Log.Error("Payload fuer " + guid + " unlesbar: " + ex.Message);
                applied[guid] = target;
            }
            if (entries == null) yield break;

            // Nur Zustaende haben sich geaendert -> nachladen statt neu bauen.
            if (current != null && mirrored.ContainsKey(guid) && StructureOf(current) == StructureOf(target))
            {
                List<GameObject> existing = mirrored[guid];
                List<GadgetBase> gadgets = new List<GadgetBase>();
                List<JObject> loaded = new List<JObject>();
                int count = Math.Min(existing.Count, entries.Count);
                for (int i = 0; i < count; i++)
                {
                    GadgetItem item = existing[i] != null ? existing[i].GetComponent<GadgetItem>() : null;
                    JObject entry = entries[i] as JObject;
                    if (item == null || item.Gadget == null || entry == null) continue;
                    gadgets.Add(item.Gadget);
                    loaded.Add(entry);
                }
                if (LoadAll(custom, gadgets, loaded, seq)) retry.Add(guid);
                else retry.Remove(guid);
                applied[guid] = target;
                yield break;
            }

            // Zusammensetzung geaendert (Gadget dazu, weg, versetzt): Vorhandenes bleibt stehen, nur der
            // Unterschied wird gebaut bzw. abgeraeumt -- sonst stuende die Lok kurz ohne Gadgets da.
            List<GameObject> old;
            mirrored.TryGetValue(guid, out old);
            Dictionary<string, Queue<GameObject>> reusable = new Dictionary<string, Queue<GameObject>>();
            if (old != null)
                foreach (GameObject go in old)
                {
                    string key;
                    GadgetItem item = go != null ? go.GetComponent<GadgetItem>() : null;
                    if (item == null || item.Gadget == null || !item.Gadget.IsLinked || !builtFrom.TryGetValue(go, out key))
                    {
                        RemoveMirrorItem(go);   // kaputt oder unbekannt: neu bauen
                        continue;
                    }
                    if (!reusable.ContainsKey(key)) reusable[key] = new Queue<GameObject>();
                    reusable[key].Enqueue(go);
                }

            List<GameObject> spawned = new List<GameObject>();
            mirrored[guid] = spawned;
            List<GadgetBase> placed = new List<GadgetBase>();
            List<JObject> placedEntries = new List<JObject>();
            List<GadgetBase> fresh = new List<GadgetBase>();

            for (int i = 0; i < entries.Count; i++)
            {
                JObject entry = entries[i] as JObject;
                if (entry == null) continue;
                string key = EntryKey(entry);

                Queue<GameObject> same;
                if (reusable.TryGetValue(key, out same) && same.Count > 0)
                {
                    GameObject keep = same.Dequeue();
                    spawned.Add(keep);
                    placed.Add(keep.GetComponent<GadgetItem>().Gadget);
                    placedEntries.Add(entry);
                    continue;
                }

                string prefabName = (string)entry["p"];
                GameObject prefab = FindItemPrefab(prefabName);
                if (prefab == null)
                {
                    Main.Log.Warning("Kein Item-Prefab fuer '" + prefabName + "'");
                    continue;
                }

                GameObject created = UnityEngine.Object.Instantiate(prefab);
                created.SetActive(true);
                StripNetworking(created);
                yield return null;   // GadgetItem.Start() muss laufen, erst danach ist .Item gesetzt

                GadgetItem createdItem = created.GetComponent<GadgetItem>();
                if (createdItem == null || createdItem.Item == null || createdItem.Gadget == null)
                {
                    Main.Log.Warning("Gadget-Item '" + prefabName + "' nicht bereit, verworfen");
                    UnityEngine.Object.Destroy(created);
                    continue;
                }

                Vector3 pos = new Vector3((float)entry["x"], (float)entry["y"], (float)entry["z"]);
                Quaternion rot = Quaternion.Euler((float)entry["rx"], (float)entry["ry"], (float)entry["rz"]);

                GadgetBase gadget = null;
                try
                {
                    gadget = GadgetItem.Place(custom, pos, rot, createdItem, null);
                }
                catch (Exception ex)
                {
                    Main.Log.Error("Place fehlgeschlagen (" + prefabName + "): " + ex.Message);
                }

                if (gadget == null)
                {
                    UnityEngine.Object.Destroy(created);
                    continue;
                }

                builtFrom[created] = key;
                spawned.Add(created);
                placed.Add(gadget);
                placedEntries.Add(entry);
                fresh.Add(gadget);
            }
            foreach (Queue<GameObject> gone in reusable.Values)
                while (gone.Count > 0) RemoveMirrorItem(gone.Dequeue());   // beim Host weggefallen

            if (LoadAll(custom, placed, placedEntries, seq, fresh)) retry.Add(guid);
            else retry.Remove(guid);
            for (int i = 0; i < fresh.Count; i++) HookControls(guid, fresh[i]);
            applied[guid] = target;
        }

        /// <summary>Art und Lage eines Eintrags -- gleich bleibende Gadgets werden daran wiedererkannt.</summary>
        private static string EntryKey(JObject entry)
        {
            System.Text.StringBuilder key = new System.Text.StringBuilder((string)entry["p"]);
            foreach (string k in new[] { "x", "y", "z", "rx", "ry", "rz" })
                key.Append('|').Append(Math.Round((double)entry[k], 3).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return key.ToString();
        }

        /// <summary>
        /// Host-Zustand auf die Spiegel eines Wagens anwenden, in vier Schritten:
        ///   1. Gadget-Zustaende ohne Kabel -- setzt dabei die Host-UIDs
        ///   2. Kabel angleichen -- erst jetzt, wo alle UIDs stehen, und nur die Unterschiede
        ///   3. Halterungen loesen und per AfterSaveDataLoaded neu aufsetzen
        ///   4. Bedienelemente stellen
        /// Gadgets mit lokaler Bedienung, die dieser Host-Stand noch nicht enthaelt, werden in 1, 2 und 4
        /// ausgelassen, sonst setzte ein aelterer Host-Zustand die Eingabe des Spielers zurueck.
        /// Rueckgabe: true, wenn etwas ausgelassen wurde -- dann holt die Schleife es nach.
        /// </summary>
        /// <param name="seq">Laufnummer dieses Host-Stands</param>
        /// <param name="fresh">gerade gebaute Spiegel: Bedienelemente springen in ihre Stellung statt zu gleiten</param>
        private bool LoadAll(Customization custom, List<GadgetBase> gadgets, List<JObject> entries, int seq, ICollection<GadgetBase> fresh = null)
        {
            bool[] keepLocal = new bool[gadgets.Count];
            bool skipped = false;
            for (int i = 0; i < gadgets.Count; i++)
            {
                keepLocal[i] = HasUnconfirmedLocalChange(gadgets[i], seq);
                skipped |= keepLocal[i];
                if (!keepLocal[i]) hostState[gadgets[i]] = entries[i];   // Basis fuers Delta: nur, was wir auch haben
            }

            GadgetState.Applying++;
            try
            {
                for (int i = 0; i < gadgets.Count; i++)
                    if (!keepLocal[i])
                        GadgetState.Load(gadgets[i], GadgetState.WithoutLinks(entries[i]["d"] as JObject), false);

                // Auch Kabel: ein aelterer Host-Stand steckte sonst ein gerade abgezogenes Kabel wieder an,
                // und der Client saehe danach keinen Unterschied mehr, den er melden koennte.
                List<GadgetBase> local = new List<GadgetBase>();
                for (int i = 0; i < gadgets.Count; i++)
                    if (keepLocal[i]) local.Add(gadgets[i]);
                for (int i = 0; i < gadgets.Count; i++)
                {
                    try
                    {
                        GadgetState.ReconcileLinks(custom, gadgets[i], entries[i]["d"] as JObject, local);
                    }
                    catch (Exception ex)
                    {
                        Main.Log.Warning("Kabel " + gadgets[i].GetType().Name + ": " + ex.Message);
                    }
                }

                // Mount.AfterSaveDataLoaded setzt das Gadget per UID neu auf und meldet sonst
                // "Mount is already occupied". UnmountGadget setzt nur die beiden Verweise zurueck.
                for (int i = 0; i < gadgets.Count; i++)
                {
                    Mount mount = gadgets[i].GetComponent<Mount>();
                    if (mount != null) mount.UnmountGadget();
                }
                for (int i = 0; i < gadgets.Count; i++)
                    GadgetState.Load(gadgets[i], GadgetState.WithoutLinks(entries[i]["d"] as JObject), true);

                for (int i = 0; i < gadgets.Count; i++)
                    if (!keepLocal[i])
                        GadgetState.ApplyControls(gadgets[i], entries[i]["c"] as JArray,
                                                  smooth: fresh == null || !fresh.Contains(gadgets[i]));
            }
            finally
            {
                GadgetState.Applying--;
            }
            return skipped;
        }

        /// <summary>Enthaelt Host-Stand Nr. seq die eigene Bedienung dieses Gadgets noch nicht?</summary>
        private bool HasUnconfirmedLocalChange(GadgetBase gadget, int seq)
        {
            if (dirty.ContainsKey(gadget)) return true;
            Inflight f;
            if (!inflight.TryGetValue(gadget, out f)) return false;
            if ((f.Acked >= f.Sent && seq >= f.Snap) || Time.time - f.Time > PENDING_TIMEOUT_SECONDS)
            {
                inflight.Remove(gadget);   // Host-Stand enthaelt alles Gesendete -- oder der Host antwortet nicht mehr
                return false;
            }
            return true;
        }

        /// <summary>Wartet an diesem Wagen noch eine Bedienung auf den Host bzw. auf den Host-Stand, der sie enthaelt?</summary>
        private bool AnyUnresolved(string guid)
        {
            List<GameObject> spawned;
            if (!mirrored.TryGetValue(guid, out spawned)) return false;
            int seq;
            wantedSeq.TryGetValue(guid, out seq);
            for (int i = 0; i < spawned.Count; i++)
            {
                GadgetItem item = spawned[i] != null ? spawned[i].GetComponent<GadgetItem>() : null;
                if (item == null || item.Gadget == null) continue;
                if (dirty.ContainsKey(item.Gadget)) return true;
                Inflight f;
                if (inflight.TryGetValue(item.Gadget, out f) && Time.time - f.Time <= PENDING_TIMEOUT_SECONDS &&
                    (f.Acked < f.Sent || seq < f.Snap)) return true;
            }
            return false;
        }

        /// <summary>Payload ohne Zustaende: nur Prefab und Position je Eintrag.</summary>
        private static string StructureOf(string payload)
        {
            try
            {
                JArray arr = JArray.Parse(payload);
                JArray stripped = new JArray();
                for (int i = 0; i < arr.Count; i++)
                {
                    JObject entry = arr[i] as JObject;
                    if (entry == null) continue;
                    JObject copy = (JObject)entry.DeepClone();
                    copy.Remove("d");
                    copy.Remove("c");
                    stripped.Add(copy);
                }
                return stripped.ToString(Formatting.None);
            }
            catch
            {
                return payload;
            }
        }

        private void ClearAllMirrors()
        {
            List<string> keys = new List<string>(mirrored.Keys);
            for (int i = 0; i < keys.Count; i++) ClearMirror(keys[i]);
            mirrored.Clear();
            wanted.Clear();
            wantedSeq.Clear();
            applied.Clear();
            dirty.Clear();
            inflight.Clear();
            hostState.Clear();
            retry.Clear();
            building.Clear();
            askText.Clear();
            builtFrom.Clear();
        }

        private void ClearMirror(string guid)
        {
            List<GameObject> spawned;
            if (!mirrored.TryGetValue(guid, out spawned)) return;

            for (int i = 0; i < spawned.Count; i++) RemoveMirrorItem(spawned[i]);

            spawned.Clear();
            mirrored.Remove(guid);
            applied.Remove(guid);
            retry.Remove(guid);
        }

        /// <summary>Ein Spiegel-Item samt Gadget abraeumen (nur beim Client, nie an den Host gemeldet).</summary>
        private void RemoveMirrorItem(GameObject go)
        {
            if (go == null) return;
            builtFrom.Remove(go);
            GadgetItem item = go.GetComponent<GadgetItem>();
            if (item != null)
            {
                if (item.Gadget != null)
                {
                    dirty.Remove(item.Gadget);
                    inflight.Remove(item.Gadget);
                    hostState.Remove(item.Gadget);
                    GadgetState.Applying++;
                    try
                    {
                        item.Gadget.Unlink();
                    }
                    catch (Exception ex)
                    {
                        Main.Log.Warning("Unlink: " + ex.Message);
                    }
                    finally
                    {
                        GadgetState.Applying--;
                    }
                    UnityEngine.Object.Destroy(item.Gadget.gameObject);
                }
                StorageController storage = SingletonBehaviour<StorageController>.Instance;
                if (storage != null && storage.StorageInstalledGadgets != null && item.Item != null)
                    storage.StorageInstalledGadgets.RemoveItem(item.Item);
            }
            UnityEngine.Object.Destroy(go);
        }

        private GameObject FindItemPrefab(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;

            if (itemPrefabs == null)
            {
                itemPrefabs = new Dictionary<string, GameObject>();
                List<InventoryItemSpec> specs = Globals.G.Items.items;
                for (int i = 0; i < specs.Count; i++)
                {
                    InventoryItemSpec spec = specs[i];
                    if (spec == null || string.IsNullOrEmpty(spec.ItemPrefabName)) continue;
                    if (spec.GetComponent<GadgetItem>() == null) continue;   // nur Gadget-Items
                    itemPrefabs[spec.ItemPrefabName] = spec.gameObject;
                }
            }

            GameObject prefab;
            return itemPrefabs.TryGetValue(prefabName, out prefab) ? prefab : null;
        }

        /// <summary>
        /// Die MP-Mod haengt jedem ItemBase in Awake ein NetworkedItem an. Unsere Spiegel-Items
        /// sind rein lokal und haetten keine NetId -- die Komponente wuerde dem Host nur Muell schicken.
        /// </summary>
        private static void StripNetworking(GameObject go)
        {
            Component[] components = go.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component c = components[i];
                if (c != null && c.GetType().Name == "NetworkedItem")
                    UnityEngine.Object.Destroy(c);
            }
        }
    }
}
