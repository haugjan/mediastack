# Caddy mit dem Azure-DNS-Modul.
#
# Warum ein eigener Build: fuer ein Wildcard-Zertifikat auf *.<deine-domain>
# braucht es die DNS-01-Challenge, also einen TXT-Record in der Zone statt
# eines erreichbaren Ports 80. Nur so bekommen auch die Dienste echte
# Zertifikate, die aus dem Internet gar nicht erreichbar sind.
#
# Das Standard-Caddy-Image bringt keine DNS-Provider mit, die muessen
# einkompiliert werden.

FROM caddy:2-builder AS builder
RUN xcaddy build --with github.com/caddy-dns/azure

FROM caddy:2-alpine
COPY --from=builder /usr/bin/caddy /usr/bin/caddy
