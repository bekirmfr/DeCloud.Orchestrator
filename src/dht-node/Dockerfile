FROM golang:1.22-alpine AS builder

WORKDIR /app
COPY go.mod go.sum ./
RUN go mod download

COPY . .
RUN CGO_ENABLED=0 GOOS=linux go build -ldflags="-s -w" -o /dht-node ./cmd/dht-node

FROM alpine:3.20
RUN apk add --no-cache ca-certificates
COPY --from=builder /dht-node /usr/local/bin/dht-node

EXPOSE 4001 5080
VOLUME /var/lib/decloud-dht

ENTRYPOINT ["/usr/local/bin/dht-node"]
