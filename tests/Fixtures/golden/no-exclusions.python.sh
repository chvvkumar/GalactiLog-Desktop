#!/usr/bin/env bash
# WBPP Session Export
# Target: M 31
# Sessions: 2026-07-01
#
# To run this script:
#   chmod +x wbpp_M_31.sh && ./wbpp_M_31.sh
#
# When it finishes, open PixInsight WBPP and use 'Add Directory' on the
# staging root: D:\Staging\M 31

set -euo pipefail

# Colors only when writing to a terminal (skipped when piped/redirected).
if [ -t 1 ]; then
    C_TITLE=$'\033[36m'; C_DIM=$'\033[90m'; C_OK=$'\033[32m'; C_RESET=$'\033[0m'
else
    C_TITLE=''; C_DIM=''; C_OK=''; C_RESET=''
fi

STAGING_ROOT='D:\Staging\M 31'
mkdir -p "$STAGING_ROOT"

TOTAL=1
printf '%s%s%s\n' "$C_TITLE" 'WBPP staging copy' "$C_RESET"
printf '%s  Target: %s%s\n' "$C_DIM" 'M 31' "$C_RESET"
printf '%s  %s session folder(s) -> %s%s\n' "$C_DIM" "$TOTAL" "$STAGING_ROOT" "$C_RESET"
printf '\n'

copy_folder() {
    # $1 = index, $2 = entry name, $3 = source dir (with trailing slash)
    printf '%s[%s/%s] %s%s\n' "$C_TITLE" "$1" "$TOTAL" "$2" "$C_RESET"
    local dest="$STAGING_ROOT/$2"
    mkdir -p "$dest"
    rsync -a --copy-links --info=progress2 "$3" "$dest/"
    printf '\n'
}

copy_folder 1 '2026-07-01' 'D:\Astro\M31\2026-07-01/'

printf '%s%s%s\n' "$C_OK" 'Done. Copied 1 folder(s).' "$C_RESET"
printf '  Open WBPP and use Add Directory on: %s\n' "$STAGING_ROOT"