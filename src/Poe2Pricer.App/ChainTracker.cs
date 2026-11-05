using Poe2Pricer.Core;
using Poe2Pricer.Core.Runes;

namespace Poe2Pricer.App;

// Keeps the rune chain to one map: once a second the game's Client.txt is read for the area the
// player entered (ClientLog), and the chain is told (RuneChain.Entered). The file lies in the
// game's folder, found through its process; until the game runs, it is looked for every
// LookForGame. Only ever read. Until the file is found the chain stays off (RuneChain.Tracked):
// the folder is unknown while the game does not run, runs as administrator while the program
// does not, or runs elsewhere (a cloud service).
public sealed class ChainTracker : IDisposable
{
    private readonly RuneChain chain;
    private readonly Action<string> log;
    private readonly Timer timer;
    private ClientLog? clientLog;
    private DateTimeOffset lookedForGame = DateTimeOffset.MinValue;
    private bool toldMissing;
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LookForGame = TimeSpan.FromSeconds(10);

    public ChainTracker(RuneChain chain, Action<string> log)
    {
        this.chain = chain;
        this.log = log;
        timer = new Timer(_ => Tick(), null, Every, Every);
    }

    private void Tick()
    {
        try
        {
            if (clientLog is null)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - lookedForGame < LookForGame)
                    return;
                lookedForGame = now;
                var folder = GameCapture.GameFolder();
                if (folder is null)
                    return;
                if (ClientLog.Find(folder) is not { } file)
                {
                    if (!toldMissing)
                        log($"Rune chain off: no logs\\Client.txt in the game's folder ({folder})");
                    toldMissing = true;
                    return;
                }
                clientLog = new ClientLog(file);
                chain.Tracked = true;
                log("Rune chain: areas from the game's Client.txt");
            }
            // One line per area the player enters (the area's id, nothing else of the file).
            if (clientLog.Poll() is { } entered)
                log(chain.Entered(entered.Area, entered.Seed) ? $"Rune chain: new map ({entered.Area}), chain starts anew" : $"Rune chain: area {entered.Area}");
        }
        catch (Exception e)
        {
            log($"Rune chain: Client.txt not read: {e.Message}");
        }
    }

    public void Dispose() => timer.Dispose();
}
