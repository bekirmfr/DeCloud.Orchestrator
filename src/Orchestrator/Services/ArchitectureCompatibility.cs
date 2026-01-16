namespace Orchestrator.Services
{
    /// <summary>
    /// Architecture compatibility helper
    /// </summary>
    public static class ArchitectureCompatibility
    {
        /// <summary>
        /// Check if a node can host a VM with given architecture requirement
        /// 
        /// SECURITY: Strict architecture matching - no cross-architecture emulation
        /// PERFORMANCE: Native architecture only for low-end devices
        /// </summary>
        public static bool IsCompatible(string nodeArchitecture, string? vmArchitecture)
        {
            // If VM doesn't specify architecture, assume x86_64 (backward compatibility)
            var requiredArch = string.IsNullOrEmpty(vmArchitecture) ? "x86_64" : vmArchitecture;

            // Normalize architecture names
            var normalizedNode = NormalizeArchitecture(nodeArchitecture);
            var normalizedVm = NormalizeArchitecture(requiredArch);

            // Strict matching - no emulation
            return normalizedNode == normalizedVm;
        }

        /// <summary>
        /// Normalize architecture names for comparison
        /// </summary>
        private static string NormalizeArchitecture(string architecture)
        {
            return architecture.ToLower() switch
            {
                "x86_64" or "amd64" or "x64" => "x86_64",
                "aarch64" or "arm64" => "aarch64",
                "i686" or "i386" or "x86" => "i686",
                "armv7l" or "armv7" or "arm" => "armv7l",
                _ => architecture.ToLower()
            };
        }

        /// <summary>
        /// Get human-readable architecture display name
        /// </summary>
        public static string GetDisplayName(string architecture)
        {
            return NormalizeArchitecture(architecture) switch
            {
                "x86_64" => "x86_64 (Intel/AMD 64-bit)",
                "aarch64" => "ARM64 (64-bit ARM)",
                "i686" => "x86 (32-bit Intel/AMD)",
                "armv7l" => "ARMv7 (32-bit ARM)",
                _ => architecture
            };
        }
    }

}
