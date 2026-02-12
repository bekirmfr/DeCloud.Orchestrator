package dht

import (
	"context"
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"sync"

	"github.com/decloud/dht-node/internal/config"
	"github.com/libp2p/go-libp2p"
	dht "github.com/libp2p/go-libp2p-kad-dht"
	pubsub "github.com/libp2p/go-libp2p-pubsub"
	"github.com/libp2p/go-libp2p/core/crypto"
	"github.com/libp2p/go-libp2p/core/host"
	"github.com/libp2p/go-libp2p/core/peer"
	"github.com/libp2p/go-libp2p/p2p/discovery/mdns"
	multiaddr "github.com/multiformats/go-multiaddr"
)

// Node is the DeCloud DHT node wrapping libp2p, Kademlia, and GossipSub.
type Node struct {
	Host   host.Host
	DHT    *dht.IpfsDHT
	PubSub *pubsub.PubSub
	Config *config.Config

	topics map[string]*pubsub.Topic
	mu     sync.RWMutex
	logger *slog.Logger
}

// DeCloud GossipSub topic names
const (
	TopicHealth = "decloud/health"
	TopicEvents = "decloud/events"
	TopicBlocks = "decloud/blocks"
)

// New creates and starts a DHT node.
func New(ctx context.Context, cfg *config.Config) (*Node, error) {
	logger := slog.Default()

	// Load or generate persistent identity
	privKey, err := loadOrCreateKey(cfg.DataDir)
	if err != nil {
		return nil, fmt.Errorf("identity key: %w", err)
	}

	// Build libp2p listen address
	listenAddr := fmt.Sprintf("/ip4/0.0.0.0/tcp/%d", cfg.ListenPort)

	// Build external address for advertisement (so other peers can find us)
	externalAddr := fmt.Sprintf("/ip4/%s/tcp/%d", cfg.AdvertiseIP, cfg.ListenPort)
	extMA, err := multiaddr.NewMultiaddr(externalAddr)
	if err != nil {
		return nil, fmt.Errorf("parse external multiaddr: %w", err)
	}

	// Create libp2p host
	// DisableRelay: WireGuard overlay handles all connectivity
	h, err := libp2p.New(
		libp2p.Identity(privKey),
		libp2p.ListenAddrStrings(listenAddr),
		libp2p.AddrsFactory(func(addrs []multiaddr.Multiaddr) []multiaddr.Multiaddr {
			// Advertise only the external address to the DHT
			return []multiaddr.Multiaddr{extMA}
		}),
		libp2p.DisableRelay(),
	)
	if err != nil {
		return nil, fmt.Errorf("create libp2p host: %w", err)
	}

	logger.Info("libp2p host created",
		"peerID", h.ID().String(),
		"listenAddr", listenAddr,
		"advertiseAddr", externalAddr,
	)

	// Create Kademlia DHT in server mode (full participant)
	kadDHT, err := dht.New(ctx, h,
		dht.Mode(dht.ModeServer),
		dht.ProtocolPrefix("/decloud"),
	)
	if err != nil {
		h.Close()
		return nil, fmt.Errorf("create kademlia DHT: %w", err)
	}

	// Bootstrap the DHT
	if err := kadDHT.Bootstrap(ctx); err != nil {
		h.Close()
		return nil, fmt.Errorf("bootstrap DHT: %w", err)
	}

	// Connect to bootstrap peers
	bootstrapCount := 0
	for _, peerAddr := range cfg.BootstrapPeers {
		ma, err := multiaddr.NewMultiaddr(peerAddr)
		if err != nil {
			logger.Warn("invalid bootstrap peer multiaddr", "addr", peerAddr, "error", err)
			continue
		}

		peerInfo, err := peer.AddrInfoFromP2pAddr(ma)
		if err != nil {
			logger.Warn("invalid bootstrap peer info", "addr", peerAddr, "error", err)
			continue
		}

		if err := h.Connect(ctx, *peerInfo); err != nil {
			logger.Warn("failed to connect to bootstrap peer", "peer", peerInfo.ID, "error", err)
			continue
		}

		bootstrapCount++
		logger.Info("connected to bootstrap peer", "peer", peerInfo.ID)
	}

	logger.Info("bootstrap complete", "connected", bootstrapCount, "total", len(cfg.BootstrapPeers))

	// Create GossipSub
	ps, err := pubsub.NewGossipSub(ctx, h)
	if err != nil {
		kadDHT.Close()
		h.Close()
		return nil, fmt.Errorf("create gossipsub: %w", err)
	}

	node := &Node{
		Host:   h,
		DHT:    kadDHT,
		PubSub: ps,
		Config: cfg,
		topics: make(map[string]*pubsub.Topic),
		logger: logger,
	}

	// Join default topics
	for _, topic := range []string{TopicHealth, TopicEvents, TopicBlocks} {
		if _, err := node.JoinTopic(topic); err != nil {
			logger.Warn("failed to join topic", "topic", topic, "error", err)
		}
	}

	// Start mDNS discovery for local network peers
	mdnsService := mdns.NewMdnsService(h, "decloud-dht", &mdnsNotifee{h: h, logger: logger})
	if err := mdnsService.Start(); err != nil {
		logger.Warn("mDNS discovery failed to start", "error", err)
	}

	return node, nil
}

// JoinTopic joins a GossipSub topic and returns it.
func (n *Node) JoinTopic(name string) (*pubsub.Topic, error) {
	n.mu.Lock()
	defer n.mu.Unlock()

	if t, ok := n.topics[name]; ok {
		return t, nil
	}

	t, err := n.PubSub.Join(name)
	if err != nil {
		return nil, err
	}

	n.topics[name] = t
	return t, nil
}

// PeerID returns this node's libp2p peer ID string.
func (n *Node) PeerID() string {
	return n.Host.ID().String()
}

// ConnectedPeers returns the number of currently connected peers.
func (n *Node) ConnectedPeers() int {
	return len(n.Host.Network().Peers())
}

// RoutingTableSize returns the number of peers in the Kademlia routing table.
func (n *Node) RoutingTableSize() int {
	return n.DHT.RoutingTable().Size()
}

// Close shuts down the DHT node.
func (n *Node) Close() error {
	n.mu.Lock()
	defer n.mu.Unlock()

	for _, t := range n.topics {
		t.Close()
	}

	if err := n.DHT.Close(); err != nil {
		n.logger.Warn("error closing DHT", "error", err)
	}

	return n.Host.Close()
}

// loadOrCreateKey loads a persistent identity key or creates a new one.
func loadOrCreateKey(dataDir string) (crypto.PrivKey, error) {
	keyPath := filepath.Join(dataDir, "identity.key")

	// Try to load existing key
	data, err := os.ReadFile(keyPath)
	if err == nil {
		key, err := crypto.UnmarshalPrivateKey(data)
		if err == nil {
			return key, nil
		}
	}

	// Generate new Ed25519 key
	priv, _, err := crypto.GenerateKeyPair(crypto.Ed25519, -1)
	if err != nil {
		return nil, err
	}

	// Persist
	if err := os.MkdirAll(dataDir, 0700); err != nil {
		return nil, err
	}

	raw, err := crypto.MarshalPrivateKey(priv)
	if err != nil {
		return nil, err
	}

	if err := os.WriteFile(keyPath, raw, 0600); err != nil {
		return nil, fmt.Errorf("save identity key: %w", err)
	}

	return priv, nil
}

// mdnsNotifee handles mDNS peer discovery on the local network.
type mdnsNotifee struct {
	h      host.Host
	logger *slog.Logger
}

func (n *mdnsNotifee) HandlePeerFound(pi peer.AddrInfo) {
	if pi.ID == n.h.ID() {
		return
	}
	n.logger.Info("mDNS discovered peer", "peer", pi.ID)
	if err := n.h.Connect(context.Background(), pi); err != nil {
		n.logger.Warn("mDNS connect failed", "peer", pi.ID, "error", err)
	}
}
