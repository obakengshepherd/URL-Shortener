#!/usr/bin/env bash
# wait-for-it.sh — wait for a TCP host:port to become available
# Usage: ./wait-for-it.sh host:port [-t timeout] [-- command args]
# Source: https://github.com/vishnubob/wait-for-it (MIT License, adapted)
#
# Used by init containers in docker-compose.yml to delay API startup
# until PostgreSQL, Redis, and Kafka are accepting connections.

WAITFORIT_cmdname=${0##*/}
WAITFORIT_timeout=30
WAITFORIT_quiet=0
WAITFORIT_host=""
WAITFORIT_port=""

echoerr() {
    if [[ $WAITFORIT_quiet -ne 1 ]]; then echo "$@" 1>&2; fi
}

usage() {
    cat << USAGE >&2
Usage:
    $WAITFORIT_cmdname host:port [-t timeout] [-- command args]
    -t TIMEOUT  Seconds to wait (default: 30)
    -q          Quiet mode
USAGE
    exit 1
}

wait_for() {
    local start_ts=$(date +%s)
    while :; do
        if [[ $WAITFORIT_port -eq 443 ]]; then
            (echo -n > /dev/tcp/$WAITFORIT_host/$WAITFORIT_port) >/dev/null 2>&1
        else
            (echo -n > /dev/tcp/$WAITFORIT_host/$WAITFORIT_port) >/dev/null 2>&1
        fi
        result=$?
        if [[ $result -eq 0 ]]; then
            local end_ts=$(date +%s)
            echoerr "$WAITFORIT_cmdname: $WAITFORIT_host:$WAITFORIT_port is available after $((end_ts - start_ts)) seconds"
            return 0
        fi
        local cur_ts=$(date +%s)
        local wait_ts=$(( cur_ts - start_ts ))
        if [[ $WAITFORIT_timeout -gt 0 && $wait_ts -ge $WAITFORIT_timeout ]]; then
            echoerr "$WAITFORIT_cmdname: timeout occurred after waiting ${WAITFORIT_timeout} seconds for $WAITFORIT_host:$WAITFORIT_port"
            return 1
        fi
        sleep 1
    done
}

# Parse arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        *:* )
            WAITFORIT_hostport=(${1//:/ })
            WAITFORIT_host=${WAITFORIT_hostport[0]}
            WAITFORIT_port=${WAITFORIT_hostport[1]}
            shift 1
            ;;
        -t)
            WAITFORIT_timeout="$2"; shift 2
            ;;
        -q)
            WAITFORIT_quiet=1; shift 1
            ;;
        --)
            shift; WAITFORIT_CLI=("$@"); break
            ;;
        *)
            echoerr "Unknown argument: $1"; usage
            ;;
    esac
done

if [[ "$WAITFORIT_host" == "" || "$WAITFORIT_port" == "" ]]; then
    echoerr "Error: you need to provide a host and port to test."
    usage
fi

wait_for
WAITFORIT_result=$?

if [[ $WAITFORIT_result -ne 0 ]]; then
    exit $WAITFORIT_result
fi

if [[ ${#WAITFORIT_CLI[@]} -ne 0 ]]; then
    exec "${WAITFORIT_CLI[@]}"
fi
