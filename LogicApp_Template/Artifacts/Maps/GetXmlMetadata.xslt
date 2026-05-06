<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
  <xsl:output method="xml" indent="yes"/>
  
  <xsl:template match="/">
    <metadata>
      <rootNode>
        <xsl:value-of select="local-name(/*)"/>
      </rootNode>
      <namespace>
        <xsl:value-of select="namespace-uri(/*)"/>
      </namespace>
    </metadata>
  </xsl:template>
</xsl:stylesheet>
