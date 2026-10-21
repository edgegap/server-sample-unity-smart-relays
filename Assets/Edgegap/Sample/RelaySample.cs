using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LightNet.Edgegap.Sample
{
    /// <summary>
    /// LightNet over the Edgegap relay: one player hosts, the others join with a lobby code, and
    /// everyone moves a cube (WASD / arrows) and chats. Nobody needs port forwarding.<br/><br/>
    /// Host flow:  lobby service POST /lobbies  -> relay server port + tokens -> StartRelayHost<br/>
    /// Join flow:  lobby service POST /lobbies/CODE/join -> relay client port + tokens -> StartRelayClient<br/><br/>
    /// Command line (for builds and automated tests):<br/>
    ///   -lobby URL                 lobby service (default http://localhost:8080)<br/>
    ///   -host                      host right away (-codeFile PATH writes the lobby code to a file)<br/>
    ///   -join CODE                 join right away<br/>
    ///   -relayAddress A -relayPort P -sessionToken S -userToken U  with -host/-client: skip the lobby,
    ///                              use tokens from edgegap_create_relay_session or the API<br/>
    ///   -client                    with the -relay* arguments: join instead of host<br/>
    ///   -maxPlayers N              players per lobby, host included (default 4)<br/>
    ///   -e2e                       client: run the reliable echo test, exit 0 on success<br/>
    ///   -expectPlayers N           with -e2e: also wait to see the cubes of N players in total<br/>
    ///   -quitWhenEmpty             host: close the lobby and quit once every client left<br/>
    ///   -quitAfter SECONDS         quit after this long (exit 1 if a test did not pass)<br/>
    ///   -loss PERCENT              drop this share of outgoing datagrams to exercise reliability
    /// </summary>
    [RequireComponent(typeof(NetworkComponent))]
    public class RelaySample : MonoBehaviour
    {
        public string LobbyUrl = "http://localhost:8080";
        public int MaxPlayers = 4;

        const int E2E_MESSAGES = 60;
        const float E2E_TIMEOUT = 60f;

        NetworkComponent Net;
        RelayNetworkService Relay;
        RelayLobbyClient Lobby;
        RelayTicket Ticket;

        string Status = "";
        string JoinCode = "";
        string ChatInput = "";
        readonly List<string> ChatLog = new List<string>();
        bool bBusy;
        bool bShowManual;
        string ManualAddress = "", ManualPort = "", ManualSession = "", ManualUser = "";

        // game state
        byte LocalPlayerId = 255;           // 0 = host, 255 = not assigned yet
        byte NextPlayerId = 1;              // host only
        readonly Dictionary<ulong, byte> PlayerIdByConnection = new Dictionary<ulong, byte>();
        readonly Dictionary<byte, Transform> Cubes = new Dictionary<byte, Transform>();
        readonly PlayerStateData OutgoingState = new PlayerStateData();
        bool bHadClients;

        // automation
        bool bE2E, bQuitWhenEmpty;
        int ExpectPlayers;
        float QuitAfter = -1f;
        string CodeFile;
        uint E2ENextExpected;
        bool bE2EStarted, bE2EDone;
        float E2EStartTime;
        double E2ERttTotalMs;
        readonly HashSet<byte> SeenPlayers = new HashSet<byte>();

        static readonly Color[] Colors = { Color.red, Color.green, new Color(0.3f, 0.5f, 1f), Color.yellow, Color.magenta, Color.cyan, new Color(1f, 0.5f, 0f), Color.white };

        void Awake()
        {
            Application.targetFrameRate = 60;
            Net = GetComponent<NetworkComponent>();
            Net.RegisterNetworkDataClass<WelcomeData>(WelcomeData.ID);
            Net.RegisterNetworkDataClass<PlayerStateData>(PlayerStateData.ID);
            Net.RegisterNetworkDataClass<PlayerLeftData>(PlayerLeftData.ID);
            Net.RegisterNetworkDataClass<ChatData>(ChatData.ID);
            Net.RegisterNetworkDataClass<EchoTestData>(EchoTestData.ID);
            Net.OnClientConnected += OnConnected;
            Net.OnClientDisconnected += OnDisconnected;
            Net.OnNetworkDataReceived += OnData;

            if (Camera.main == null)
            {
                GameObject cam = new GameObject("Main Camera");
                cam.tag = "MainCamera";
                cam.AddComponent<Camera>();
                cam.transform.SetPositionAndRotation(new Vector3(0f, 8f, -8f), Quaternion.Euler(45f, 0f, 0f));
            }
        }

        void Start()
        {
            string[] args = Environment.GetCommandLineArgs();
            LobbyUrl = Arg(args, "-lobby") ?? LobbyUrl;
            int.TryParse(Arg(args, "-maxPlayers") ?? MaxPlayers.ToString(), out MaxPlayers);
            bE2E = Has(args, "-e2e");
            bQuitWhenEmpty = Has(args, "-quitWhenEmpty");
            int.TryParse(Arg(args, "-expectPlayers") ?? "0", out ExpectPlayers);
            float.TryParse(Arg(args, "-quitAfter") ?? "-1", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out QuitAfter);
            CodeFile = Arg(args, "-codeFile");
            Lobby = new RelayLobbyClient(LobbyUrl);

            if (Arg(args, "-relayAddress") != null)
            {
                ManualAddress = Arg(args, "-relayAddress");
                ManualPort = Arg(args, "-relayPort") ?? "";
                ManualSession = Arg(args, "-sessionToken") ?? "";
                ManualUser = Arg(args, "-userToken") ?? "";
                StartManual(!Has(args, "-client"));
            }
            else if (Has(args, "-host"))
            {
                HostLobby();
            }
            else if (Arg(args, "-join") != null)
            {
                JoinCode = Arg(args, "-join");
                JoinLobby();
            }
        }

        // ------------------------------------------------------------ lobby + relay

        void HostLobby()
        {
            bBusy = true;
            Status = "Creating lobby and relay session...";
            StartCoroutine(Lobby.Host(MaxPlayers, ticket =>
            {
                bBusy = false;
                if (!string.IsNullOrEmpty(ticket.error))
                {
                    Status = "Lobby error: " + ticket.error;
                    Debug.LogError(Status);
                    return;
                }
                Ticket = ticket;
                Debug.Log($"LOBBY CODE: {ticket.code} (relay session {ticket.session_id}, relay {ticket.relay_host})");
                if (!string.IsNullOrEmpty(CodeFile))
                {
                    File.WriteAllText(CodeFile, ticket.code);
                }
                StartRelay(ticket.ToEndpoint(), true);
            }));
        }

        void JoinLobby()
        {
            if (string.IsNullOrWhiteSpace(JoinCode))
            {
                Status = "Enter the lobby code from the host.";
                return;
            }
            bBusy = true;
            Status = $"Joining lobby {JoinCode.ToUpperInvariant()}...";
            StartCoroutine(Lobby.Join(JoinCode, ticket =>
            {
                bBusy = false;
                if (!string.IsNullOrEmpty(ticket.error))
                {
                    Status = "Lobby error: " + ticket.error;
                    Debug.LogError(Status);
                    return;
                }
                Ticket = ticket;
                StartRelay(ticket.ToEndpoint(), false);
            }));
        }

        void StartManual(bool host)
        {
            if (!int.TryParse(ManualPort, out int port) || !uint.TryParse(ManualSession, out uint session) || !uint.TryParse(ManualUser, out uint user))
            {
                Status = "Manual relay: port and tokens must be numbers.";
                Debug.LogError(Status);
                return;
            }
            StartRelay(new RelayEndpoint { Address = ManualAddress, Port = port, SessionAuthorizationToken = session, UserAuthorizationToken = user }, host);
        }

        void StartRelay(RelayEndpoint endpoint, bool host)
        {
            Relay = host ? Net.StartRelayHost(endpoint, MaxPlayers - 1) : Net.StartRelayClient(endpoint);
            if (Relay == null)
            {
                Status = "Could not start relay networking, see the log.";
                return;
            }
            string[] args = Environment.GetCommandLineArgs();
            if (int.TryParse(Arg(args, "-loss") ?? "0", out int loss) && loss > 0)
            {
                Net.SetNetworkErrorEmulation(loss, 0);
                Debug.Log($"Emulating {loss}% outgoing packet loss.");
            }
            if (host)
            {
                LocalPlayerId = 0;
                SpawnCube(0);
            }
            Status = host ? "Connecting to the relay..." : "Connecting to the host through the relay...";
        }

        void Leave()
        {
            if (Net.GetState() == ENetworkState.Running || Net.GetState() == ENetworkState.Startup)
            {
                Net.Close();
            }
            if (Ticket != null && Ticket.role == "host" && !string.IsNullOrEmpty(Ticket.host_secret))
            {
                // relay sessions bill while open
                StartCoroutine(Lobby.Close(Ticket));
            }
            Ticket = null;
            ResetGame();
            Status = "Left.";
        }

        void ResetGame()
        {
            foreach (Transform cube in Cubes.Values)
            {
                if (cube != null)
                {
                    Destroy(cube.gameObject);
                }
            }
            Cubes.Clear();
            PlayerIdByConnection.Clear();
            LocalPlayerId = 255;
            NextPlayerId = 1;
        }

        // ------------------------------------------------------------ network events

        void OnConnected(object sender, ConnectionEventArgs e)
        {
            if (Net.IsServer())
            {
                byte id = NextPlayerId++;
                PlayerIdByConnection[e.Handle.GetInternalHandle()] = id;
                bHadClients = true;
                Net.SendNetworkData(e.Handle, ENetChannel.Reliable, new WelcomeData { PlayerId = id });
                AddChat($"Player {id} joined.");
                Debug.Log($"Player {id} joined ({PlayerIdByConnection.Count} client(s) connected)");
            }
            else
            {
                Status = "Connected through the relay.";
            }
        }

        void OnDisconnected(object sender, ConnectionEventArgs e)
        {
            if (Net.IsServer())
            {
                if (PlayerIdByConnection.TryGetValue(e.Handle.GetInternalHandle(), out byte id))
                {
                    PlayerIdByConnection.Remove(e.Handle.GetInternalHandle());
                    RemoveCube(id);
                    Net.BroadcastNetworkData(ENetChannel.Reliable, new PlayerLeftData { PlayerId = id });
                    AddChat($"Player {id} left.");
                    Debug.Log($"Player {id} left ({PlayerIdByConnection.Count} client(s) connected)");
                }
            }
            else
            {
                Status = "Disconnected from the host.";
                ResetGame();
            }
        }

        void OnData(object sender, ReceivedNetworkDataEventArgs e)
        {
            switch (e.Type)
            {
                case WelcomeData.ID:
                {
                    LocalPlayerId = ((WelcomeData)e.Data).PlayerId;
                    SpawnCube(LocalPlayerId);
                    Debug.Log($"Joined as player {LocalPlayerId}");
                    break;
                }

                case PlayerStateData.ID:
                {
                    PlayerStateData state = (PlayerStateData)e.Data;
                    if (Net.IsServer())
                    {
                        // trust the connection, not the claimed id
                        if (!PlayerIdByConnection.TryGetValue(e.Connection.GetInternalHandle(), out byte id))
                        {
                            return;
                        }
                        state.PlayerId = id;
                        foreach (ConnectionHandle other in Net.GetConnections())
                        {
                            if (other.GetInternalHandle() != e.Connection.GetInternalHandle())
                            {
                                Net.SendNetworkData(other, ENetChannel.Unreliable, state);
                            }
                        }
                    }
                    if (state.PlayerId != LocalPlayerId)
                    {
                        SeenPlayers.Add(state.PlayerId);
                        Transform cube = SpawnCube(state.PlayerId);
                        cube.SetPositionAndRotation(state.Position, state.Rotation);
                    }
                    break;
                }

                case PlayerLeftData.ID:
                {
                    RemoveCube(((PlayerLeftData)e.Data).PlayerId);
                    break;
                }

                case ChatData.ID:
                {
                    ChatData chat = (ChatData)e.Data;
                    if (Net.IsServer())
                    {
                        if (!PlayerIdByConnection.TryGetValue(e.Connection.GetInternalHandle(), out byte id))
                        {
                            return;
                        }
                        chat.PlayerId = id;
                        Net.BroadcastNetworkData(ENetChannel.Reliable, chat);
                    }
                    AddChat($"P{chat.PlayerId}: {chat.Text}");
                    break;
                }

                case EchoTestData.ID:
                {
                    EchoTestData echo = (EchoTestData)e.Data;
                    if (Net.IsServer())
                    {
                        Net.SendNetworkData(e.Connection, ENetChannel.Reliable, echo);
                    }
                    else
                    {
                        CheckEcho(echo);
                    }
                    break;
                }
            }
        }

        // ------------------------------------------------------------ game loop

        void Update()
        {
            if (Relay != null && Net.GetState() == ENetworkState.Closed && Relay.GetLastError() != null && !Status.StartsWith("Relay"))
            {
                Status = "Relay: " + Relay.GetLastError();
            }
            else if (Relay != null && Net.IsServer() && Net.GetState() == ENetworkState.Running && Status.StartsWith("Connecting"))
            {
                Status = "Hosting. Share the lobby code.";
            }

            if (Net.GetState() == ENetworkState.Running && LocalPlayerId != 255 && Cubes.TryGetValue(LocalPlayerId, out Transform me))
            {
                Vector3 move = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
                if (bE2E)
                {
                    move = new Vector3(Mathf.Cos(Time.time), 0f, Mathf.Sin(Time.time));
                }
                me.position += move * 3f * Time.deltaTime;
                me.Rotate(0f, move.x * 90f * Time.deltaTime, 0f);

                OutgoingState.PlayerId = LocalPlayerId;
                OutgoingState.Position = me.position;
                OutgoingState.Rotation = me.rotation;
                Net.BroadcastNetworkData(ENetChannel.Unreliable, OutgoingState);
            }

            UpdateAutomation();
        }

        void UpdateAutomation()
        {
            if (bE2E && !bE2EStarted && LocalPlayerId != 255 && !Net.IsServer())
            {
                bE2EStarted = true;
                E2EStartTime = Time.realtimeSinceStartup;
                for (uint i = 0; i < E2E_MESSAGES; ++i)
                {
                    // mostly small, some above one datagram, one large: exercises fragmentation too
                    int size = i == E2E_MESSAGES / 2 ? 20000 : (i % 10 == 9 ? 3000 : 64);
                    Net.SendNetworkData(Net.GetConnections()[0], ENetChannel.Reliable, new EchoTestData
                    {
                        Sequence = i,
                        SentTicks = DateTime.UtcNow.Ticks,
                        Payload = EchoTestData.MakePayload(i, size)
                    });
                }
                Debug.Log($"E2E: sent {E2E_MESSAGES} reliable messages to the host");
            }

            if (bE2EStarted && !bE2EDone)
            {
                bool bEchoesDone = E2ENextExpected == E2E_MESSAGES;
                bool bPlayersDone = SeenPlayers.Count >= ExpectPlayers - 1;
                if (bEchoesDone && bPlayersDone)
                {
                    bE2EDone = true;
                    Debug.Log($"E2E PASS: {E2E_MESSAGES}/{E2E_MESSAGES} reliable echoes in order, avg round trip {E2ERttTotalMs / E2E_MESSAGES:F0} ms, saw cubes of players [{string.Join(",", SeenPlayers)}], {Relay.GetResendCount()} resends");
                    StartCoroutine(QuitSoon(0));
                }
                else if (Time.realtimeSinceStartup - E2EStartTime > E2E_TIMEOUT)
                {
                    bE2EDone = true;
                    Debug.LogError($"E2E FAIL: {E2ENextExpected}/{E2E_MESSAGES} echoes, saw players [{string.Join(",", SeenPlayers)}] of {ExpectPlayers - 1} expected");
                    StartCoroutine(QuitSoon(1));
                }
            }

            if (bQuitWhenEmpty && bHadClients && Net.IsServer() && PlayerIdByConnection.Count == 0)
            {
                bQuitWhenEmpty = false;
                Debug.Log("All clients left, closing the lobby.");
                StartCoroutine(QuitSoon(0));
            }

            if (QuitAfter > 0f && Time.realtimeSinceStartup > QuitAfter)
            {
                QuitAfter = -1f;
                Debug.LogWarning("-quitAfter reached.");
                StartCoroutine(QuitSoon(bE2E && !bE2EDone ? 1 : 0));
            }
        }

        void CheckEcho(EchoTestData echo)
        {
            if (echo.Sequence != E2ENextExpected)
            {
                Debug.LogError($"E2E: echo {echo.Sequence} arrived, expected {E2ENextExpected} (out of order!)");
                return;
            }
            int size = echo.Sequence == E2E_MESSAGES / 2 ? 20000 : (echo.Sequence % 10 == 9 ? 3000 : 64);
            byte[] expected = EchoTestData.MakePayload(echo.Sequence, size);
            if (echo.Payload.Length != expected.Length)
            {
                Debug.LogError($"E2E: echo {echo.Sequence} has {echo.Payload.Length} bytes, expected {expected.Length}");
                return;
            }
            for (int i = 0; i < expected.Length; ++i)
            {
                if (echo.Payload[i] != expected[i])
                {
                    Debug.LogError($"E2E: echo {echo.Sequence} payload corrupted at byte {i}");
                    return;
                }
            }
            E2ERttTotalMs += (DateTime.UtcNow.Ticks - echo.SentTicks) / (double)TimeSpan.TicksPerMillisecond;
            E2ENextExpected++;
        }

        IEnumerator QuitSoon(int exitCode)
        {
            if (Net.GetState() == ENetworkState.Running || Net.GetState() == ENetworkState.Startup)
            {
                Net.Close();
            }
            if (Ticket != null && Ticket.role == "host" && !string.IsNullOrEmpty(Ticket.host_secret))
            {
                yield return Lobby.Close(Ticket);
                Debug.Log($"Closed lobby {Ticket.code}");
            }
            yield return new WaitForSeconds(0.5f);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(exitCode);
#endif
        }

        Transform SpawnCube(byte playerId)
        {
            if (Cubes.TryGetValue(playerId, out Transform cube) && cube != null)
            {
                return cube;
            }
            cube = GameObject.CreatePrimitive(PrimitiveType.Cube).transform;
            cube.name = "Player " + playerId;
            cube.position = new Vector3((playerId % 4) * 2f - 3f, 0.5f, (playerId / 4) * 2f);
            Renderer renderer = cube.GetComponent<Renderer>();
            renderer.material.color = Colors[playerId % Colors.Length];
            Cubes[playerId] = cube;
            return cube;
        }

        void RemoveCube(byte playerId)
        {
            if (Cubes.TryGetValue(playerId, out Transform cube))
            {
                Cubes.Remove(playerId);
                if (cube != null)
                {
                    Destroy(cube.gameObject);
                }
            }
        }

        void AddChat(string line)
        {
            ChatLog.Add(line);
            if (ChatLog.Count > 8)
            {
                ChatLog.RemoveAt(0);
            }
        }

        void OnApplicationQuit()
        {
            // best effort; the lobby service also expires lobbies
            if (Ticket != null && Ticket.role == "host")
            {
                StartCoroutine(Lobby.Close(Ticket));
            }
        }

        // ------------------------------------------------------------ UI

        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, 360, Screen.height - 20), GUI.skin.box);
            GUILayout.Label("<b>LightNet over Edgegap Relay</b>", new GUIStyle(GUI.skin.label) { richText = true });

            ENetworkState state = Net.GetState();
            if (state == ENetworkState.Closed && !bBusy)
            {
                GUILayout.Label("Lobby service URL");
                LobbyUrl = GUILayout.TextField(LobbyUrl);
                Lobby.BaseUrl = LobbyUrl.TrimEnd('/');

                if (GUILayout.Button("Host"))
                {
                    HostLobby();
                }
                GUILayout.BeginHorizontal();
                JoinCode = GUILayout.TextField(JoinCode, 8, GUILayout.Width(120));
                if (GUILayout.Button("Join with code"))
                {
                    JoinLobby();
                }
                GUILayout.EndHorizontal();

                bShowManual = GUILayout.Toggle(bShowManual, "Manual relay tokens (testing)");
                if (bShowManual)
                {
                    ManualAddress = LabeledField("Relay IP / host", ManualAddress);
                    ManualPort = LabeledField("Port (server/client)", ManualPort);
                    ManualSession = LabeledField("Session token", ManualSession);
                    ManualUser = LabeledField("User token", ManualUser);
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Host (server port)"))
                    {
                        StartManual(true);
                    }
                    if (GUILayout.Button("Join (client port)"))
                    {
                        StartManual(false);
                    }
                    GUILayout.EndHorizontal();
                }
            }
            else if (state != ENetworkState.Closed)
            {
                if (Ticket != null && !string.IsNullOrEmpty(Ticket.code))
                {
                    GUILayout.Label($"Lobby code: <b>{Ticket.code}</b>", new GUIStyle(GUI.skin.label) { richText = true, fontSize = 18 });
                }
                GUILayout.Label($"Network: {state}   Relay: {(Relay != null ? Relay.GetRelayState().ToString() : "-")}");
                GUILayout.Label(Net.IsServer() ? $"Host, {PlayerIdByConnection.Count} client(s)" : $"Player {(LocalPlayerId == 255 ? "?" : LocalPlayerId.ToString())}");

                foreach (string line in ChatLog)
                {
                    GUILayout.Label(line);
                }
                if (state == ENetworkState.Running)
                {
                    GUILayout.BeginHorizontal();
                    ChatInput = GUILayout.TextField(ChatInput, 100);
                    if (GUILayout.Button("Send", GUILayout.Width(60)) && ChatInput.Length > 0)
                    {
                        SendChat(ChatInput);
                        ChatInput = "";
                    }
                    GUILayout.EndHorizontal();
                }
                if (GUILayout.Button("Leave"))
                {
                    Leave();
                }
            }

            GUILayout.Label(Status);
            GUILayout.FlexibleSpace();
            GUILayout.Label("Move: WASD / arrows");
            GUILayout.EndArea();
        }

        void SendChat(string text)
        {
            ChatData chat = new ChatData { PlayerId = LocalPlayerId, Text = text };
            if (Net.IsServer())
            {
                Net.BroadcastNetworkData(ENetChannel.Reliable, chat);
                AddChat($"P0: {text}");
            }
            else
            {
                Net.BroadcastNetworkData(ENetChannel.Reliable, chat);
            }
        }

        static string LabeledField(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(130));
            value = GUILayout.TextField(value);
            GUILayout.EndHorizontal();
            return value;
        }

        static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; ++i)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return null;
        }

        static bool Has(string[] args, string name)
        {
            return Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
