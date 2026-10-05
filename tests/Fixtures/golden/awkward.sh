#!/usr/bin/env bash
# WBPP Session Export
# Target: M 81 - Bode's Galaxy
# Sessions: 2026-07-01
#
# To run this script:
#   chmod +x wbpp_M_81_-_Bode_s_Galaxy.sh && ./wbpp_M_81_-_Bode_s_Galaxy.sh
# Paths are written for a WSL mount, where the drive C: appears as /mnt/c. Edit the /mnt prefix if your own mount differs.
#
# When it finishes, open PixInsight WBPP and use 'Add Directory' on the
# staging root: /mnt/d/WBPP Staging/o'brien

set -euo pipefail

# Colors only when writing to a terminal (skipped when piped/redirected).
if [ -t 1 ]; then
    C_TITLE=$'\033[36m'; C_DIM=$'\033[90m'; C_OK=$'\033[32m'; C_RESET=$'\033[0m'
else
    C_TITLE=''; C_DIM=''; C_OK=''; C_RESET=''
fi

STAGING_ROOT='/mnt/d/WBPP Staging/o'\''brien'
mkdir -p -- "$STAGING_ROOT"

TOTAL=1
printf '%s%s%s\n' "$C_TITLE" 'WBPP staging copy' "$C_RESET"
printf '%s  Target: %s%s\n' "$C_DIM" 'M 81 - Bode'\''s Galaxy' "$C_RESET"
printf '%s  %s session folder(s) -> %s%s\n' "$C_DIM" "$TOTAL" "$STAGING_ROOT" "$C_RESET"
printf '\n'

copy_folder() {
    # $1 = index, $2 = entry name, $3 = source dir (with trailing slash), $4+ = extra rsync args
    printf '%s[%s/%s] %s%s\n' "$C_TITLE" "$1" "$TOTAL" "$2" "$C_RESET"
    local dest="$STAGING_ROOT/$2"
    mkdir -p -- "$dest"
    rsync -a --copy-links --info=progress2 --exclude="WBPP" --exclude="*CALIBRATED" "${@:4}" -- "$3" "$dest/"
    printf '\n'
}

copy_folder 1 '2026-07-01' '//nas/astro/o'\''brien $HOME/Angle_71.61/' --exclude='/Ha/it'\''s bad.fits'

printf '%s%s%s\n' "$C_OK" 'Done. Copied 1 folder(s).' "$C_RESET"
printf '  Open WBPP and use Add Directory on: %s\n' "$STAGING_ROOT"