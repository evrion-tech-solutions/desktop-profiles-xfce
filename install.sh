#!/bin/bash
set -e

# DesktopProfiles for Xfce Installer
APP_NAME="desktop-profiles-xfce"
BIN_NAME="DesktopProfiles.Xfce.Gui"
INSTALL_DIR="/usr/local/bin/$APP_NAME"
ICON_DIR="/usr/share/icons/hicolor/512x512/apps"
desktop_file="/usr/share/applications/$APP_NAME.desktop"

if [ "$EUID" -ne 0 ]; then 
  echo "Please run as root"
  exit 1
fi

echo "Installing $APP_NAME..."

# Create installation directory
mkdir -p "$INSTALL_DIR"

# Copy binary and resources
cp -r ./* "$INSTALL_DIR/"

# Install icon
mkdir -p "$ICON_DIR"
cp "$INSTALL_DIR/Assets/app-icon.png" "$ICON_DIR/$APP_NAME.png"

# Create .desktop file
cat <<EOF > "$desktop_file"
[Desktop Entry]
Name=Desktop Profiles
Comment=Automatic wallpaper manager for Xfce
Exec=$INSTALL_DIR/$BIN_NAME
Icon=$APP_NAME
Terminal=false
Type=Application
Categories=Utility;Settings;DesktopSettings;
X-XFCE-Autostart-enabled=true
EOF

# Set permissions
chmod +x "$INSTALL_DIR/$BIN_NAME"
chmod +x "$desktop_file"

echo "Installation complete!"
echo "Run 'desktop-profiles-xfce' from your application menu or terminal."
