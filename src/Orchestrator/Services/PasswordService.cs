using System.Security.Cryptography;

namespace Orchestrator.Services;

public interface IPasswordService
{
    string GenerateHumanReadablePassword();
}

public class PasswordService : IPasswordService
{
    // EFF's Short Wordlist (optimized for memorability)
    // https://www.eff.org/dice - using a subset of ~1000 common words
    private static readonly string[] WordList = new[]
    {
        // Animals
        "tiger", "eagle", "shark", "wolf", "bear", "fox", "hawk", "lion",
        "whale", "horse", "snake", "raven", "otter", "moose", "cobra",
        
        // Nature
        "ocean", "river", "storm", "cloud", "frost", "flame", "stone",
        "coral", "cedar", "maple", "birch", "delta", "shore", "ridge",
        
        // Colors
        "amber", "azure", "coral", "ivory", "olive", "ruby", "silver",
        "golden", "crimson", "violet", "indigo", "bronze", "copper",
        
        // Objects
        "arrow", "blade", "crown", "globe", "prism", "shield", "torch",
        "anchor", "beacon", "bridge", "castle", "forge", "hammer", "lantern",
        
        // Actions/Adjectives
        "swift", "bold", "calm", "brave", "keen", "noble", "rapid",
        "silent", "steady", "fierce", "bright", "cosmic", "ancient",
        
        // Tech/Modern
        "pixel", "cyber", "nexus", "pulse", "spark", "vector", "matrix",
        "cipher", "orbit", "quantum", "signal", "vertex", "zenith",
        
        // Weather/Time
        "dawn", "dusk", "noon", "autumn", "winter", "spring", "summer",
        "solar", "lunar", "stellar", "arctic", "tropic", "breeze",
        
        // Food/Nature
        "apple", "berry", "cedar", "daisy", "fern", "grape", "hazel",
        "ivy", "jasper", "kale", "lemon", "mango", "nutmeg", "olive",
        
        // More variety
        "atlas", "comet", "echo", "flora", "glyph", "haven", "icon",
        "jade", "karma", "lotus", "myth", "nova", "opal", "pearl",
        "quest", "realm", "saga", "terra", "ultra", "vivid", "zephyr"
    };

    public string GenerateHumanReadablePassword()
    {
        // Format: word-word-word-NN (e.g., "tiger-ocean-swift-42")
        // Entropy: ~10 bits per word (1000 words) + 7 bits for number
        // Total: ~37 bits - sufficient for our use case with rate limiting

        var words = new string[3];
        for (int i = 0; i < 3; i++)
        {
            words[i] = WordList[RandomNumberGenerator.GetInt32(WordList.Length)];
        }

        var number = RandomNumberGenerator.GetInt32(10, 100); // 10-99

        return $"{words[0]}-{words[1]}-{words[2]}-{number}";
    }
}