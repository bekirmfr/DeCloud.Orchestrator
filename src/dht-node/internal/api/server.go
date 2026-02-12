package api

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"time"

	dhtnode "github.com/decloud/dht-node/internal/dht"
	cid "github.com/ipfs/go-cid"
	mh "github.com/multiformats/go-multihash"
)

// Server is the localhost HTTP API that the node agent uses to interact
// with the DHT VM. It is NOT exposed to the network — only localhost.
type Server struct {
	node   *dhtnode.Node
	mux    *http.ServeMux
	server *http.Server
	logger *slog.Logger
}

// NewServer creates the HTTP API server.
func NewServer(node *dhtnode.Node, port int) *Server {
	s := &Server{
		node:   node,
		mux:    http.NewServeMux(),
		logger: slog.Default(),
	}

	s.mux.HandleFunc("GET /health", s.handleHealth)
	s.mux.HandleFunc("GET /peers", s.handlePeers)
	s.mux.HandleFunc("GET /peer/{peerID}", s.handlePeer)

	s.mux.HandleFunc("GET /dht/get/{key...}", s.handleDHTGet)
	s.mux.HandleFunc("PUT /dht/put/{key...}", s.handleDHTPut)
	s.mux.HandleFunc("GET /dht/providers/{key...}", s.handleDHTFindProviders)
	s.mux.HandleFunc("POST /dht/provide/{key...}", s.handleDHTProvide)

	s.mux.HandleFunc("POST /pubsub/publish/{topic...}", s.handlePubSubPublish)

	s.server = &http.Server{
		Addr:         fmt.Sprintf("127.0.0.1:%d", port),
		Handler:      s.mux,
		ReadTimeout:  10 * time.Second,
		WriteTimeout: 30 * time.Second,
	}

	return s
}

// Start begins serving the HTTP API.
func (s *Server) Start() error {
	s.logger.Info("HTTP API listening", "addr", s.server.Addr)
	return s.server.ListenAndServe()
}

// Shutdown gracefully stops the HTTP server.
func (s *Server) Shutdown(ctx context.Context) error {
	return s.server.Shutdown(ctx)
}

// ──────────────────────────────────────────────────────────────
// Health & Peer endpoints
// ──────────────────────────────────────────────────────────────

type HealthResponse struct {
	PeerID           string `json:"peerId"`
	ConnectedPeers   int    `json:"connectedPeers"`
	RoutingTableSize int    `json:"routingTableSize"`
	Status           string `json:"status"`
}

func (s *Server) handleHealth(w http.ResponseWriter, r *http.Request) {
	status := "active"
	if s.node.ConnectedPeers() == 0 && s.node.RoutingTableSize() == 0 {
		status = "initializing"
	}

	writeJSON(w, HealthResponse{
		PeerID:           s.node.PeerID(),
		ConnectedPeers:   s.node.ConnectedPeers(),
		RoutingTableSize: s.node.RoutingTableSize(),
		Status:           status,
	})
}

type PeerInfo struct {
	PeerID string   `json:"peerId"`
	Addrs  []string `json:"addrs"`
}

func (s *Server) handlePeers(w http.ResponseWriter, r *http.Request) {
	peers := s.node.Host.Network().Peers()
	result := make([]PeerInfo, 0, len(peers))

	for _, p := range peers {
		addrs := s.node.Host.Peerstore().Addrs(p)
		addrStrs := make([]string, len(addrs))
		for i, a := range addrs {
			addrStrs[i] = a.String()
		}
		result = append(result, PeerInfo{PeerID: p.String(), Addrs: addrStrs})
	}

	writeJSON(w, result)
}

func (s *Server) handlePeer(w http.ResponseWriter, r *http.Request) {
	peerIDStr := r.PathValue("peerID")

	peers := s.node.Host.Network().Peers()
	for _, p := range peers {
		if p.String() == peerIDStr {
			addrs := s.node.Host.Peerstore().Addrs(p)
			addrStrs := make([]string, len(addrs))
			for i, a := range addrs {
				addrStrs[i] = a.String()
			}
			writeJSON(w, PeerInfo{PeerID: p.String(), Addrs: addrStrs})
			return
		}
	}

	http.Error(w, "peer not found", http.StatusNotFound)
}

// ──────────────────────────────────────────────────────────────
// DHT key-value endpoints
// ──────────────────────────────────────────────────────────────

func (s *Server) handleDHTGet(w http.ResponseWriter, r *http.Request) {
	key := "/" + r.PathValue("key")

	ctx, cancel := context.WithTimeout(r.Context(), 15*time.Second)
	defer cancel()

	value, err := s.node.DHT.GetValue(ctx, key)
	if err != nil {
		http.Error(w, fmt.Sprintf("DHT get failed: %v", err), http.StatusNotFound)
		return
	}

	w.Header().Set("Content-Type", "application/octet-stream")
	w.Write(value)
}

func (s *Server) handleDHTPut(w http.ResponseWriter, r *http.Request) {
	key := "/" + r.PathValue("key")

	body, err := io.ReadAll(io.LimitReader(r.Body, 1<<20)) // 1MB limit
	if err != nil {
		http.Error(w, "read body failed", http.StatusBadRequest)
		return
	}

	ctx, cancel := context.WithTimeout(r.Context(), 15*time.Second)
	defer cancel()

	if err := s.node.DHT.PutValue(ctx, key, body); err != nil {
		http.Error(w, fmt.Sprintf("DHT put failed: %v", err), http.StatusInternalServerError)
		return
	}

	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) handleDHTFindProviders(w http.ResponseWriter, r *http.Request) {
	key := r.PathValue("key")

	ctx, cancel := context.WithTimeout(r.Context(), 15*time.Second)
	defer cancel()

	c := keyToCID(key)

	providers := s.node.DHT.FindProvidersAsync(ctx, c, 20)

	result := make([]PeerInfo, 0)
	for p := range providers {
		addrStrs := make([]string, len(p.Addrs))
		for i, a := range p.Addrs {
			addrStrs[i] = a.String()
		}
		result = append(result, PeerInfo{PeerID: p.ID.String(), Addrs: addrStrs})
	}

	writeJSON(w, result)
}

func (s *Server) handleDHTProvide(w http.ResponseWriter, r *http.Request) {
	key := r.PathValue("key")

	ctx, cancel := context.WithTimeout(r.Context(), 15*time.Second)
	defer cancel()

	c := keyToCID(key)

	if err := s.node.DHT.Provide(ctx, c, true); err != nil {
		http.Error(w, fmt.Sprintf("DHT provide failed: %v", err), http.StatusInternalServerError)
		return
	}

	w.WriteHeader(http.StatusNoContent)
}

// ──────────────────────────────────────────────────────────────
// PubSub endpoint
// ──────────────────────────────────────────────────────────────

func (s *Server) handlePubSubPublish(w http.ResponseWriter, r *http.Request) {
	topicName := r.PathValue("topic")

	body, err := io.ReadAll(io.LimitReader(r.Body, 1<<20))
	if err != nil {
		http.Error(w, "read body failed", http.StatusBadRequest)
		return
	}

	topic, err := s.node.JoinTopic(topicName)
	if err != nil {
		http.Error(w, fmt.Sprintf("join topic failed: %v", err), http.StatusInternalServerError)
		return
	}

	ctx, cancel := context.WithTimeout(r.Context(), 10*time.Second)
	defer cancel()

	if err := topic.Publish(ctx, body); err != nil {
		http.Error(w, fmt.Sprintf("publish failed: %v", err), http.StatusInternalServerError)
		return
	}

	w.WriteHeader(http.StatusNoContent)
}

// ──────────────────────────────────────────────────────────────
// Helpers
// ──────────────────────────────────────────────────────────────

// keyToCID converts a string key (e.g., a block hash) to a CID
// for use with Kademlia provider records (PROVIDE / FIND_PROVIDERS).
func keyToCID(key string) cid.Cid {
	hash := sha256.Sum256([]byte(key))
	encoded, _ := mh.Encode(hash[:], mh.SHA2_256)
	return cid.NewCidV1(cid.Raw, encoded)
}

func writeJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(v)
}
