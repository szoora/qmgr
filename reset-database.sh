#!/bin/bash

echo "========================================"
echo "Q-Mgr Database Reset Script"
echo "========================================"
echo ""
echo "WARNING: This will delete all existing data!"
read -p "Press Enter to continue or Ctrl+C to cancel..."

cd "src/Q-Mgr.API"

echo ""
echo "Step 1: Dropping database..."
dotnet ef database drop --force
if [ $? -ne 0 ]; then
    echo "ERROR: Failed to drop database. Make sure applications are stopped."
    read -p "Press Enter to exit..."
    exit 1
fi

echo ""
echo "Step 2: Applying migrations..."
dotnet ef database update
if [ $? -ne 0 ]; then
    echo "ERROR: Failed to apply migrations."
    read -p "Press Enter to exit..."
    exit 1
fi

echo ""
echo "========================================"
echo "Database reset complete!"
echo "========================================"
echo ""
echo "SuperAdmin credentials:"
echo "  Email: superadmin@qmgr.platform"
echo "  Password: super123"
echo ""
echo "Admin credentials:"
echo "  Email: admin@qmgr.demo"
echo "  Password: admin123"
echo ""
echo "Staff credentials:"
echo "  Email: agent1@qmgr.demo"
echo "  Password: agent123"
echo ""
echo "Now run the applications:"
echo "  Terminal 1: cd src/Q-Mgr.API && dotnet run"
echo "  Terminal 2: cd src/Q-Mgr.Web && dotnet run"
echo ""
read -p "Press Enter to exit..."
