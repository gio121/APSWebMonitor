import sqlite3
import json

def update_colors():
    conn = sqlite3.connect('aps.db')
    cursor = conn.cursor()
    
    cursor.execute("SELECT Id, ContentJson FROM Windows WHERE ContentJson IS NOT NULL AND ContentJson != ''")
    rows = cursor.fetchall()
    
    updated_count = 0
    for row in rows:
        win_id = row[0]
        content = row[1]
        try:
            elements = json.loads(content)
            changed = False
            for el in elements:
                # Si el color es blanco o el naranja por defecto antiguo, pasar a negro
                # Tambien comprobamos si es un icono segun el usuario
                
                # Campos de color a revisar
                color_fields = ["Color", "ActiveColor", "ErrorColor"]
                for field in color_fields:
                    val = el.get(field, "").lower()
                    if val in ["#ffffff", "white", "#ff9800"]:
                        el[field] = "#000000"
                        changed = True
                
                # Adicionalmente, si el tipo NO es uno de los exceptuados y el color no es negro, lo ponemos negro
                t = el.get("Type", "")
                if t not in ["Etiqueta", "Señal", "Caja"]:
                    if el.get("Color") != "#000000":
                        el["Color"] = "#000000"
                        changed = True
            
            if changed:
                new_content = json.dumps(elements)
                cursor.execute("UPDATE Windows SET ContentJson = ? WHERE Id = ?", (new_content, win_id))
                updated_count += 1
                print(f"Window {win_id} updated.")
                
        except Exception as e:
            print(f"Error parsing Window {win_id}: {e}")
            
    conn.commit()
    conn.close()
    print(f"Successfully updated {updated_count} windows.")

if __name__ == '__main__':
    update_colors()
