# If you change this please also update GO_VERSION in Makefile (then run
# `make lint` to see where else it needs to be updated as well).
FROM golang:1.23.6-alpine as builder

# Force Go to use the cgo based DNS resolver. This is required to ensure DNS
# queries required to connect to linked containers succeed.
ENV GODEBUG netdns=cgo

# Pass a tag, branch or a commit using build-arg.  This allows a docker
# image to be built from a specified Git state.  The default image
# will use the Git tip of master by default.
ARG checkout="v0.19.0-beta"
ARG git_url="https://github.com/lightningnetwork/lnd"

# Install dependencies and build the binaries.
RUN apk add --no-cache --update alpine-sdk \
    git \
    make \
    gcc \
&&  git clone $git_url /go/src/github.com/lightningnetwork/lnd \
&&  cd /go/src/github.com/lightningnetwork/lnd \
&&  git checkout $checkout \
&&  make release-install -j ${nprocs}
#
## Start a new, final image.
#FROM alpine as final
#
## Define a root volume for data persistence.
#VOLUME /root/.lnd
#
## Add utilities for quality of life and SSL-related reasons. We also require
## curl and gpg for the signature verification script.
#RUN apk --no-cache add \
#    bash \
#    jq \
#    ca-certificates \
#    gnupg \
#    curl

# Copy the binaries from the builder image.




FROM debian:stable-slim

ARG LND_VERSION=0.19.0-beta.rc1
ENV PATH=/opt/lnd:$PATH

RUN apt-get update -y \
  && apt-get install -y curl gosu wait-for-it \
  && apt-get clean \
  && rm -rf /var/lib/apt/lists/* /tmp/* /var/tmp/*

#RUN SYS_ARCH="$(dpkg --print-architecture)" \
#  && curl -SLO https://github.com/lightningnetwork/lnd/releases/download/v${LND_VERSION}/lnd-linux-${SYS_ARCH}-v${LND_VERSION}.tar.gz \
#  && tar -xzf *.tar.gz \
#  && mkdir /opt/lnd \
#  && mv ./lnd-linux-${SYS_ARCH}-v${LND_VERSION}/* /opt/lnd \
#  && rm *.tar.gz
RUN SYS_ARCH="$(dpkg --print-architecture)" \
    && mkdir /opt/lnd
COPY --from=builder /go/bin/lncli /opt/lnd/
COPY --from=builder /go/bin/lnd /opt/lnd/
COPY --from=builder /go/src/github.com/lightningnetwork/lnd/scripts/verify-install.sh /
COPY --from=builder /go/src/github.com/lightningnetwork/lnd/scripts/keys/* /keys/


RUN curl -SLO https://raw.githubusercontent.com/lightningnetwork/lnd/master/contrib/lncli.bash-completion \
  && mkdir /etc/bash_completion.d \
  && mv lncli.bash-completion /etc/bash_completion.d/ \
  && curl -SLO https://raw.githubusercontent.com/scop/bash-completion/master/bash_completion \
  && mv bash_completion /usr/share/bash-completion/

COPY docker-entrypoint.sh /entrypoint.sh
COPY bashrc /home/lnd/.bashrc

RUN chmod a+x /entrypoint.sh

VOLUME ["/home/lnd/.lnd"]

EXPOSE 9735 8080 10000

ENTRYPOINT ["/entrypoint.sh"]

CMD ["lnd"]