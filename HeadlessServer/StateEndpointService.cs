using System.Net;
using System.Text;
using System.Text.Json;
using Core; // IAddonReader, PlayerReader live here
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace HeadlessServer;

public sealed class StateEndpointService : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly IAddonReader _addonReader;
    private readonly PlayerReader _playerReader; // ← NEW: injected
    private readonly ILogger<StateEndpointService> _logger;
    private Task? _listenTask;
    private readonly CancellationTokenSource _cts = new();

    public StateEndpointService(
        IAddonReader addonReader,
        PlayerReader playerReader,               // ← DI must resolve this
        GoapAgent goapAgent,
        ILogger<StateEndpointService> logger)
    {
        _addonReader = addonReader ?? throw new ArgumentNullException(nameof(addonReader));
        _playerReader = playerReader ?? throw new ArgumentNullException(nameof(playerReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _listener.Prefixes.Add("http://localhost:8080/");
    }
    private readonly GoapAgent _goapAgent;

    public void Start()
    {
        try
        {
            _listener.Start();
            _listenTask = Task.Run(ListenLoopAsync, _cts.Token);
            _logger.LogInformation("✅ State endpoint STARTED → http://localhost:8080/state");
            Console.WriteLine("✅ [STATE ENDPOINT] Listening on http://localhost:8080/state");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start State endpoint");
            Console.WriteLine($"❌ [STATE ENDPOINT] FAILED TO START: {ex.Message}");
            Console.WriteLine(ex.ToString());
        }
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                await HandleRequestAsync(context);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Listener error");
                Console.WriteLine($"❌ [STATE ENDPOINT] Listener error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/state")
        {
            try
            {
                var state = BuildStateDto();
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                var buffer = Encoding.UTF8.GetBytes(json);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = buffer.Length;
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [STATE ENDPOINT] Error building JSON: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            return;
        }

        context.Response.StatusCode = 404;
        context.Response.Close();
    }

    private object BuildStateDto()
    {
        double playerX = 0.0;
        double playerY = 0.0;
        double playerZ = 0.0;
        int healthPct = 100;
        int manaPct = 100;
        int freeSlots = 16;
        int durabilityPct = 100;
        bool isMounted = false;
        bool inCombat = false;
        string botStatus = "Running";

        try
        {
            // Force refresh from the current addon buffer (critical!)
            _playerReader.Update(_addonReader);

            // Read position (already working)
            playerX = _playerReader.MapX;
            playerY = _playerReader.MapY;
            playerZ = _playerReader.WorldPosZ;

            // Health & Mana – log raw values to debug why % is stuck
            int healthCurrent = _playerReader.HealthCurrent();
            int healthMax = _playerReader.HealthMax();
            int manaCurrent = _playerReader.ManaCurrent();
            int manaMax = _playerReader.ManaMax();

            healthPct = _playerReader.HealthPercent();
            manaPct = _playerReader.ManaPercent();

            durabilityPct = _playerReader.AvgEquipDurability();

            // Debug logging – watch these in console
            Console.WriteLine($"[STATE DTO] Raw Health: {healthCurrent}/{healthMax} → {healthPct}%");
            Console.WriteLine($"[STATE DTO] Raw Mana:   {manaCurrent}/{manaMax}   → {manaPct}%");
            Console.WriteLine($"[STATE DTO] Durability: {durabilityPct}% | Pos: {playerX:F2}, {playerY:F2}");

            // Mounted / InCombat – try if bits is public
            // if (_playerReader.bits != null) {
            //     isMounted = _playerReader.bits.IsMounted();
            //     inCombat  = _playerReader.bits.InCombat();
            // }

            // Optional: better status based on state
            if (_playerReader.IsCasting()) botStatus = "Casting";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read/update PlayerReader");
            Console.WriteLine($"[STATE DTO] Error: {ex.Message}");
        }

        return new
        {
            Timestamp = DateTime.UtcNow,
            Position = new { X = playerX, Y = playerY, Z = playerZ },
            HealthPercent = healthPct,
            ManaPercent = manaPct,
            BagSlotsFree = freeSlots,
            DurabilityPercent = durabilityPct,
            BotStatus = botStatus,
            LastGoal = "Unknown (check logs)",
            IsMounted = isMounted,
            InCombat = inCombat
        };
    }

    public void Stop()
    {
        try
        {
            _cts.Cancel();
            _listener.Stop();
            _listenTask?.Wait(1000);
            Console.WriteLine("🛑 [STATE ENDPOINT] Stopped");
        }
        catch { }
    }

    public void Dispose() => Stop();
}