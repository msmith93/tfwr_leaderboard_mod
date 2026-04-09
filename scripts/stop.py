#!/usr/bin/env python3
"""Stop any active execution in the game."""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
import api
print(api.stop())
