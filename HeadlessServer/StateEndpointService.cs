using System.Net;
using System.Text;
using System.Text.Json;
using Core; // IAddonReader, PlayerReader live here
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Core.GOAP;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;

namespace HeadlessServer;

public sealed class StateEndpointService : IDisposable
{
    
    private readonly HttpListener _listener = new();
    private readonly IAddonReader _addonReader;
    private readonly PlayerReader _playerReader; // ← NEW: injected
    // private readonly IServiceProvider _serviceProvider;
    private readonly IBotController _botController;
    private readonly ILogger<StateEndpointService> _logger;
    private Task? _listenTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly AddonBits _bits;
    private readonly BagReader _bagReader;

    public StateEndpointService(
        IAddonReader addonReader,
        PlayerReader playerReader,               // ← DI must resolve this
        AddonBits bits,
        BagReader bagReader,
        // IServiceProvider serviceProvider,
        IBotController botController,
        ILogger<StateEndpointService> logger)
    {
        _addonReader = addonReader ?? throw new ArgumentNullException(nameof(addonReader));
        _playerReader = playerReader ?? throw new ArgumentNullException(nameof(playerReader));
        //_serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _bits = bits ?? throw new ArgumentNullException(nameof(bits));
        _bagReader = bagReader ?? throw new ArgumentNullException(nameof(bagReader));
        _botController = botController ?? throw new ArgumentNullException(nameof(botController));
        
        _listener.Prefixes.Add("http://localhost:8080/");

    }

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
        int freeSlots = _bagReader != null ? _bagReader.Bags.Sum(b => b.FreeSlot) : 16;
        int durabilityPct = 100;
        bool isMounted = false;
        bool inCombat = false;
        string botStatus = "Running";
        string goalName = "None/Idle";
        string zoneName = _playerReader.ZoneName;

        // Declare variables once
        double secondsInGoal = 0;
        var recentGoals = Array.Empty<string>();
        int killsLast10Min = 0;
        int foodCount = 0;
        int drinkCount = 0;

        try
        {
            _addonReader.Update();

            if (_addonReader is IAddonDataProvider dataProvider)
            {
                _playerReader.Update(dataProvider);
            }
            else
            {
                Console.WriteLine("[STATE DTO] Warning: IAddonReader is not IAddonDataProvider");
            }

            playerX = _playerReader.MapX;
            playerY = _playerReader.MapY;
            playerZ = _playerReader.WorldPosZ;

            int healthCurrent = _playerReader.HealthCurrent();
            int healthMax = _playerReader.HealthMax();
            int manaCurrent = _playerReader.ManaCurrent();
            int manaMax = _playerReader.ManaMax();

            healthPct = _playerReader.HealthPercent();
            manaPct = _playerReader.ManaPercent();

            durabilityPct = _playerReader.AvgEquipDurability();

            Console.WriteLine($"[STATE DTO] Raw Health: {healthCurrent}/{healthMax} → {healthPct}%");
            Console.WriteLine($"[STATE DTO] Raw Mana: {manaCurrent}/{manaMax} → {manaPct}%");
            Console.WriteLine($"[STATE DTO] Durability: {durabilityPct}% | Pos: {playerX:F2}, {playerY:F2}, {playerZ:F2}");

            if (_bits == null)
            {
                Console.WriteLine("[STATE DTO] Warning: AddonBits is null");
                isMounted = false;
                inCombat = false;
            }
            else
            {
                isMounted = _bits.Mounted();
                inCombat = _bits.Combat();
                Console.WriteLine($"[STATE DTO] Mounted: {isMounted} | InCombat: {inCombat}");
            }

            if (inCombat)
                botStatus = "InCombat";
            else if (_playerReader.IsCasting())
                botStatus = "Casting";
            else if (_bits.Dead())
                botStatus = "Dead";

            var goapAgent = _botController.GoapAgent;

            Console.WriteLine($"[STATE DTO] GoapAgent resolved OK: {goapAgent != null}, Active: {goapAgent?.Active}");

            secondsInGoal = goapAgent?.SecondsInCurrentGoal ?? 0;
            recentGoals = goapAgent?.RecentGoals ?? Array.Empty<string>();
            killsLast10Min = goapAgent?.KillsLast10Min ?? 0;

            foodCount = _bagReader?.FoodItemCount() ?? 0;
            drinkCount = _bagReader?.DrinkItemCount() ?? 0;

            zoneName = _playerReader.ZoneName;

            if (goapAgent?.CurrentGoal != null)
            {
                goalName = goapAgent.CurrentGoal.Name ?? string.Empty;
                if (string.IsNullOrWhiteSpace(goalName))
                {
                    goalName = goapAgent.CurrentGoal.GetType().Name.Replace("Goal", "").Trim();
                }

                goalName = goalName switch
                {
                    var n when n.Contains("Adhoc") => "Adhoc (vendor/repair/eat/wait/etc)",
                    var n when n.Contains("FollowRoute") => "FollowRoute (main grind path)",
                    var n when n.Contains("Combat") => "Combat",
                    var n when n.Contains("Loot") => "Loot",
                    var n when n.Contains("NPC") => "NPC (vendor/mail)",
                    var n when n.Contains("Pull") => "Pull",
                    var n when n.Contains("Skinning") => "Skinning",
                    var n when n.Contains("Gather") => "Gathering",
                    _ => goalName
                };
                
                Console.WriteLine($"[STATE DTO] Current Goal: {goalName}");
            }
            else
            {
                Console.WriteLine("[STATE DTO] No active CurrentGoal");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed reading state");
            Console.WriteLine($"[STATE DTO] Error: {ex.Message}");

            // Minimal return in catch
            return new
            {
                Timestamp = DateTime.UtcNow,
                Error = "Failed to build state: " + ex.Message
            };
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
            CurrentGoal = goalName,
            IsMounted = isMounted,
            InCombat = inCombat,

            SecondsInCurrentGoal = secondsInGoal,
            RecentGoals = (string[])recentGoals.ToArray(),
            KillsLast10Min = killsLast10Min,
            FoodCount = foodCount,
            DrinkCount = drinkCount,
            ZoneName = zoneName
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