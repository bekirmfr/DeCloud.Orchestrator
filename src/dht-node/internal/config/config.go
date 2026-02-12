package config

import (
	"fmt"
	"os"
	"strconv"
	"strings"
)

// Config holds all DHT node configuration, populated from environment variables
// set by the orchestrator via cloud-init labels.
type Config struct {
	// ListenPort is the libp2p TCP port (default: 4001)
	ListenPort int

	// APIPort is the localhost HTTP API port for the node agent (default: 5080)
	APIPort int

	// AdvertiseIP is the IP other nodes use to reach this DHT VM
	AdvertiseIP string

	// BootstrapPeers is a list of libp2p multiaddrs for initial DHT bootstrap
	// Format: "/ip4/{ip}/tcp/4001/p2p/{peerId}"
	BootstrapPeers []string

	// NodeID is the DeCloud node ID (for logging/metadata)
	NodeID string

	// Region is the node's region (for metadata)
	Region string

	// DataDir is where the DHT node stores persistent state (peer ID key, datastore)
	DataDir string
}

// LoadFromEnv reads configuration from environment variables.
// These are set by the orchestrator's cloud-init via VM labels.
func LoadFromEnv() (*Config, error) {
	cfg := &Config{
		ListenPort: 4001,
		APIPort:    5080,
		DataDir:    "/var/lib/decloud-dht",
	}

	if port := os.Getenv("DHT_LISTEN_PORT"); port != "" {
		p, err := strconv.Atoi(port)
		if err != nil {
			return nil, fmt.Errorf("invalid DHT_LISTEN_PORT: %w", err)
		}
		cfg.ListenPort = p
	}

	if port := os.Getenv("DHT_API_PORT"); port != "" {
		p, err := strconv.Atoi(port)
		if err != nil {
			return nil, fmt.Errorf("invalid DHT_API_PORT: %w", err)
		}
		cfg.APIPort = p
	}

	cfg.AdvertiseIP = os.Getenv("DHT_ADVERTISE_IP")
	if cfg.AdvertiseIP == "" {
		return nil, fmt.Errorf("DHT_ADVERTISE_IP is required")
	}

	if peers := os.Getenv("DHT_BOOTSTRAP_PEERS"); peers != "" {
		for _, p := range strings.Split(peers, ",") {
			p = strings.TrimSpace(p)
			if p != "" {
				cfg.BootstrapPeers = append(cfg.BootstrapPeers, p)
			}
		}
	}

	cfg.NodeID = os.Getenv("DECLOUD_NODE_ID")
	cfg.Region = os.Getenv("DECLOUD_REGION")

	if dir := os.Getenv("DHT_DATA_DIR"); dir != "" {
		cfg.DataDir = dir
	}

	return cfg, nil
}
