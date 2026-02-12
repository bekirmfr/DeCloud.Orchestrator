package main

import (
	"context"
	"log/slog"
	"os"
	"os/signal"
	"syscall"

	"github.com/decloud/dht-node/internal/api"
	"github.com/decloud/dht-node/internal/config"
	"github.com/decloud/dht-node/internal/dht"
)

func main() {
	logger := slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo}))
	slog.SetDefault(logger)

	logger.Info("DeCloud DHT node starting")

	// Load configuration from environment (set by cloud-init from orchestrator labels)
	cfg, err := config.LoadFromEnv()
	if err != nil {
		logger.Error("failed to load configuration", "error", err)
		os.Exit(1)
	}

	logger.Info("configuration loaded",
		"listenPort", cfg.ListenPort,
		"apiPort", cfg.APIPort,
		"advertiseIP", cfg.AdvertiseIP,
		"bootstrapPeers", len(cfg.BootstrapPeers),
		"nodeID", cfg.NodeID,
		"region", cfg.Region,
	)

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	// Create and start the DHT node
	node, err := dht.New(ctx, cfg)
	if err != nil {
		logger.Error("failed to create DHT node", "error", err)
		os.Exit(1)
	}
	defer node.Close()

	logger.Info("DHT node started",
		"peerID", node.PeerID(),
		"connectedPeers", node.ConnectedPeers(),
	)

	// Start the localhost HTTP API for node agent communication
	apiServer := api.NewServer(node, cfg.APIPort)
	go func() {
		if err := apiServer.Start(); err != nil {
			logger.Error("HTTP API server failed", "error", err)
			cancel()
		}
	}()

	// Wait for shutdown signal
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)

	select {
	case sig := <-sigCh:
		logger.Info("received shutdown signal", "signal", sig)
	case <-ctx.Done():
		logger.Info("context cancelled")
	}

	logger.Info("shutting down")

	shutdownCtx, shutdownCancel := context.WithTimeout(context.Background(), 10*1e9)
	defer shutdownCancel()

	if err := apiServer.Shutdown(shutdownCtx); err != nil {
		logger.Error("HTTP API shutdown error", "error", err)
	}

	logger.Info("DHT node stopped")
}
