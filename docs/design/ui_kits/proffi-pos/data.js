// Proffi POS — sample apparel catalog for the UI kit demo.
// Clothing retail: each product carries size / color / season so the order panel
// can show the richer, more informative line items apparel stores need.
window.PROFFI_DATA = {
  categories: [
    { id: "all", name: "ALL", icon: "view-grid" },
    { id: "tops", name: "Tops", icon: "tshirt-crew" },
    { id: "bottoms", name: "Bottoms", icon: "hanger" },
    { id: "outerwear", name: "Outerwear", icon: "coat-rack" },
    { id: "footwear", name: "Footwear", icon: "shoe-sneaker" },
    { id: "accessories", name: "Accessories", icon: "sunglasses" },
  ],
  products: [
    { id: 1, name: "Oxford Cotton Shirt", sku: "TOP-1102", price: 149.00, cat: "tops", icon: "tshirt-crew", size: "M", color: { name: "Sky", hex: "#7dd3fc" }, season: "SS'25" },
    { id: 2, name: "Merino Knit Sweater", sku: "TOP-2210", price: 219.00, originalPrice: 259.00, discountPercent: 15, cat: "tops", icon: "tshirt-crew", size: "L", color: { name: "Camel", hex: "#c9a66b" }, season: "FW'24" },
    { id: 3, name: "Silk Blend Blouse", sku: "TOP-3055", price: 179.00, cat: "tops", icon: "tshirt-v", size: "S", color: { name: "Ivory", hex: "#f5f0e6" }, season: "SS'25" },
    { id: 4, name: "Slim Chino Trousers", sku: "BOT-4021", price: 189.00, cat: "bottoms", icon: "hanger", size: "32", color: { name: "Olive", hex: "#6b7150" }, season: "SS'25" },
    { id: 5, name: "Raw Denim Jeans", sku: "BOT-4890", price: 245.00, cat: "bottoms", icon: "hanger", size: "34", color: { name: "Indigo", hex: "#334876" }, season: "FW'24" },
    { id: 6, name: "Pleated Midi Skirt", sku: "BOT-5140", price: 165.00, originalPrice: 199.00, discountPercent: 17, cat: "bottoms", icon: "hanger", size: "M", color: { name: "Blush", hex: "#e6b7bd" }, season: "SS'25" },
    { id: 7, name: "Wool Overcoat", sku: "OUT-6300", price: 590.00, cat: "outerwear", icon: "coat-rack", size: "L", color: { name: "Charcoal", hex: "#3a3f4a" }, season: "FW'24" },
    { id: 8, name: "Quilted Field Jacket", sku: "OUT-6712", price: 420.00, cat: "outerwear", icon: "coat-rack", size: "M", color: { name: "Forest", hex: "#3f5545" }, season: "FW'24" },
    { id: 9, name: "Leather Derby Shoes", sku: "FTW-7150", price: 320.00, cat: "footwear", icon: "shoe-formal", size: "42", color: { name: "Brown", hex: "#6b4a34" }, season: "FW'24" },
    { id: 10, name: "Canvas Low Sneakers", sku: "FTW-7803", price: 129.00, originalPrice: 149.00, discountPercent: 13, cat: "footwear", icon: "shoe-sneaker", size: "43", color: { name: "White", hex: "#f1f5f9" }, season: "SS'25" },
    { id: 11, name: "Leather Belt", sku: "ACC-8020", price: 79.00, cat: "accessories", icon: "belt", size: "One", color: { name: "Tan", hex: "#c08a5a" }, season: "All" },
    { id: 12, name: "Cashmere Scarf", sku: "ACC-8560", price: 139.00, cat: "accessories", icon: "scarf", size: "One", color: { name: "Grey", hex: "#94a3b8" }, season: "FW'24" },
  ],
};
